// Stage 7 RED contract for directory-document lifecycle deletion.

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Services;

namespace SaddleRAG.Tests.Monitor;

[Trait("Category", "Integration")]
public sealed class DocumentLifecycleDeletionTests : IAsyncLifetime
{
    private string mDatabaseName = string.Empty;
    private SaddleRagDbContext mContext = new(Options.Create(new SaddleRagDbSettings()));
    private LibraryRepository mLibraries = null!;
    private PageRepository mPages = null!;
    private ChunkRepository mChunks = null!;
    private SourceDocumentRepository mSources = null!;
    private SubjectAssignmentRepository mAssignments = null!;
    private SubjectCatalogRepository mCatalogs = null!;
    private LibraryDeletionService mDeletion = null!;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-stage7-delete-{Guid.NewGuid():N}";
        mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings
                                                            {
                                                                ConnectionString = TestConnectionString,
                                                                DatabaseName = mDatabaseName
                                                            }));
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        mLibraries = new LibraryRepository(mContext);
        mPages = new PageRepository(mContext);
        mChunks = new ChunkRepository(mContext);
        mSources = new SourceDocumentRepository(mContext);
        mAssignments = new SubjectAssignmentRepository(mContext);
        mCatalogs = new SubjectCatalogRepository(mContext);

        var factory = Substitute.For<RepositoryFactory>([null!]);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(mLibraries);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(mChunks);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(mPages);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(new LibraryProfileRepository(mContext));
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(new LibraryIndexRepository(mContext));
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(new Bm25ShardRepository(mContext));
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(new ExcludedSymbolsRepository(mContext));
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(new ScrapeAuditRepository(mContext));
        factory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(mSources);
        factory.GetSubjectAssignmentRepository(Arg.Any<string?>()).Returns(mAssignments);
        factory.GetSubjectCatalogRepository(Arg.Any<string?>()).Returns(mCatalogs);
        mDeletion = new LibraryDeletionService(factory, new InMemoryBruteForceVectorSearch());
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
    }

    [Fact]
    public async Task DeleteVersionRemovesVersionMetadataAndOnlyArtifactsWhoseFinalReferenceWasDeleted()
    {
        await SeedTwoVersionDirectoryLibraryAsync();

        LibraryDeletionResult result = await mDeletion.DeleteVersionAsync(profile: null,
                                                                            LibraryId,
                                                                            FirstVersion,
                                                                            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DocumentRevisions);
        Assert.Null(await mSources.GetRevisionAsync(FirstRevisionId,
                                                     TestContext.Current.CancellationToken));
        Assert.NotNull(await mSources.GetRevisionAsync(SecondRevisionId,
                                                        TestContext.Current.CancellationToken));
        Assert.NotNull(await mSources.GetDocumentAsync(DocumentId,
                                                        TestContext.Current.CancellationToken));
        IReadOnlyList<SubjectAssignmentRecord> assignments =
            await mAssignments.GetByDocumentRevisionIdsAsync([FirstRevisionId, SecondRevisionId],
                                                              TestContext.Current.CancellationToken);
        SubjectAssignmentRecord remainingAssignment = Assert.Single(assignments);
        Assert.Equal(SecondRevisionId, remainingAssignment.DocumentRevisionId);
        Assert.NotNull(await mCatalogs.GetAsync(LibraryId,
                                                 TaxonomyVersion,
                                                 TestContext.Current.CancellationToken));
        Assert.NotNull(await mSources.GetDirectoryDefinitionAsync(LibraryId,
                                                                   TestContext.Current.CancellationToken));
        Assert.Empty(await mPages.GetPagesAsync(LibraryId,
                                                FirstVersion,
                                                TestContext.Current.CancellationToken));
        Assert.Empty(await mChunks.GetChunksAsync(LibraryId,
                                                  FirstVersion,
                                                  TestContext.Current.CancellationToken));
        Assert.Single(await mPages.GetPagesAsync(LibraryId,
                                                 SecondVersion,
                                                 TestContext.Current.CancellationToken));
        Assert.Single(await mChunks.GetChunksAsync(LibraryId,
                                                   SecondVersion,
                                                   TestContext.Current.CancellationToken));
        Assert.Equal(SharedOriginalBytes, await ReadArtifactAsync(Hash(SharedOriginalBytes)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mSources.OpenArtifactAsync(
                                                                 Hash(FirstExtractionBytes),
                                                                 TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteLibraryRemovesDirectorySubjectsAndFinalArtifactReference()
    {
        await SeedTwoVersionDirectoryLibraryAsync();

        LibraryDeletionResult result = await mDeletion.DeleteLibraryAsync(profile: null,
                                                                            LibraryId,
                                                                            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.DocumentRevisions);
        Assert.Null(await mLibraries.GetLibraryAsync(LibraryId,
                                                      TestContext.Current.CancellationToken));
        Assert.Null(await mSources.GetDirectoryDefinitionAsync(LibraryId,
                                                                TestContext.Current.CancellationToken));
        Assert.Null(await mSources.GetDocumentAsync(DocumentId,
                                                     TestContext.Current.CancellationToken));
        Assert.Null(await mSources.GetRevisionAsync(FirstRevisionId,
                                                     TestContext.Current.CancellationToken));
        Assert.Null(await mSources.GetRevisionAsync(SecondRevisionId,
                                                     TestContext.Current.CancellationToken));
        Assert.Empty(await mAssignments.GetByDocumentRevisionIdsAsync([FirstRevisionId, SecondRevisionId],
                                                                       TestContext.Current.CancellationToken));
        Assert.Null(await mCatalogs.GetAsync(LibraryId,
                                              TaxonomyVersion,
                                              TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mSources.OpenArtifactAsync(
                                                                 Hash(SharedOriginalBytes),
                                                                 TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mSources.OpenArtifactAsync(
                                                                 Hash(FirstExtractionBytes),
                                                                 TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountArtifactFilesAsync());
    }

    private async Task SeedTwoVersionDirectoryLibraryAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await mLibraries.UpsertLibraryAsync(new LibraryRecord
                                                {
                                                    Id = LibraryId,
                                                    Name = "Owned manuals",
                                                    Hint = "stage 7",
                                                    CurrentVersion = SecondVersion,
                                                    AllVersions = [FirstVersion, SecondVersion]
                                                },
                                            ct);
        await mLibraries.UpsertVersionAsync(Version(FirstVersion), ct);
        await mLibraries.UpsertVersionAsync(Version(SecondVersion), ct);
        await mSources.UpsertDirectoryDefinitionAsync(new DirectoryLibraryDefinition
                                                          {
                                                              Id = LibraryId,
                                                              RootPath = RootPath,
                                                              Recursive = true,
                                                              AllowedExtensions = [".pdf", ".docx"],
                                                              ExclusionPatterns = ["**/bin/**"],
                                                              BindingStatus = DirectoryLibraryBindingStatus.Bound,
                                                              RegisteredAtUtc = RecordedAt
                                                          },
                                                      ct);
        await mSources.GetOrCreateDocumentAsync(SourceDocument(), ct);
        await PersistRevisionAsync(Revision(FirstVersion,
                                            FirstRevisionId,
                                            SharedOriginalBytes,
                                            FirstExtractionBytes),
                                   SharedOriginalBytes,
                                   FirstExtractionBytes);
        await PersistRevisionAsync(Revision(SecondVersion,
                                            SecondRevisionId,
                                            SharedOriginalBytes,
                                            extraction: null),
                                   SharedOriginalBytes,
                                   extraction: null);
        await mCatalogs.InsertRevisionAsync(Catalog(), ct);
        await mAssignments.PersistAsync(Assignment(FirstVersion, FirstRevisionId), ct);
        await mAssignments.PersistAsync(Assignment(SecondVersion, SecondRevisionId), ct);
        await mPages.UpsertPageAsync(Page(FirstVersion, FirstRevisionId), ct);
        await mPages.UpsertPageAsync(Page(SecondVersion, SecondRevisionId), ct);
        await mChunks.InsertChunksAsync([Chunk(FirstVersion, FirstRevisionId)], ct);
        await mChunks.InsertChunksAsync([Chunk(SecondVersion, SecondRevisionId)], ct);
    }

    private async Task PersistRevisionAsync(DocumentRevisionRecord revision,
                                             byte[] original,
                                             byte[]? extraction)
    {
        await using var originalStream = new MemoryStream(original, writable: false);
        await using var extractionStream = extraction == null
                                               ? null
                                               : new MemoryStream(extraction, writable: false);
        await mSources.PersistRevisionAsync(revision,
                                             originalStream,
                                             extractionStream,
                                             TestContext.Current.CancellationToken);
    }

    private async Task<byte[]> ReadArtifactAsync(string hash)
    {
        await using Stream stream = await mSources.OpenArtifactAsync(hash,
                                                                      TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    private async Task<long> CountArtifactFilesAsync()
    {
        IMongoCollection<MongoDB.Bson.BsonDocument> files =
            mContext.Database.GetCollection<MongoDB.Bson.BsonDocument>("documentArtifacts.files");
        return await files.CountDocumentsAsync(FilterDefinition<MongoDB.Bson.BsonDocument>.Empty,
                                               cancellationToken: TestContext.Current.CancellationToken);
    }

    private static LibraryVersionRecord Version(string version) => new()
        {
            Id = $"{LibraryId}/{version}",
            LibraryId = LibraryId,
            Version = version,
            ScrapedAt = RecordedAt,
            PageCount = 1,
            ChunkCount = 1,
            EmbeddingProviderId = "stage7",
            EmbeddingModelName = "stage7",
            EmbeddingDimensions = 2,
            PublicationState = VersionPublicationState.Published
        };

    private static SourceDocumentRecord SourceDocument() => new()
        {
            Id = DocumentId,
            LibraryId = LibraryId,
            NormalizedRelativePath = RelativePath,
            DisplayRelativePath = RelativePath,
            DisplayName = "manual.pdf",
            SourceUri = SourceUri,
            MediaType = "application/pdf",
            FirstSeenVersion = FirstVersion,
            LastSeenVersion = SecondVersion,
            CreatedAtUtc = RecordedAt,
            UpdatedAtUtc = RecordedAt
        };

    private static DocumentRevisionRecord Revision(string version,
                                                   string revisionId,
                                                   byte[] original,
                                                   byte[]? extraction) => new()
        {
            Id = revisionId,
            DocumentId = DocumentId,
            LibraryId = LibraryId,
            Version = version,
            ScanRunId = $"scan-{version}",
            State = DocumentRevisionState.Published,
            SourceModifiedAtUtc = RecordedAt,
            AcquiredAtUtc = RecordedAt,
            OriginalArtifactHash = Hash(original),
            OriginalByteLength = original.LongLength,
            OriginalMediaType = "application/pdf",
            ExtractionArtifactHash = extraction == null ? null : Hash(extraction),
            ExtractionByteLength = extraction?.LongLength,
            ExtractionMediaType = extraction == null ? null : "application/json",
            ExtractionProvenance = extraction == null
                                       ? null
                                       : new DocumentExtractionProvenance
                                             {
                                                 ExtractorName = "docling",
                                                 ExtractorVersion = "2.50.0",
                                                 ConfigurationHash = "stage7-config",
                                                 UsedOcr = true,
                                                 QualityScore = 0.98,
                                                 Warnings = ["fixture warning"]
                                             },
            PublishedAtUtc = RecordedAt
        };

    private static SubjectCatalogRecord Catalog() => new()
        {
            Id = SubjectCatalogRepository.MakeId(LibraryId, TaxonomyVersion),
            LibraryId = LibraryId,
            Revision = 1,
            TaxonomyVersion = TaxonomyVersion,
            Concepts =
            [
                new SubjectConcept
                    {
                        Id = SubjectId,
                        Label = "Hydraulics",
                        Aliases = ["fluid power"],
                        Description = "Hydraulic service procedures"
                    }
            ],
            Provenance = ClassifierProvenance(),
            CreatedAtUtc = RecordedAt
        };

    private static SubjectAssignmentRecord Assignment(string version, string revisionId) => new()
        {
            Id = SubjectAssignmentRepository.MakeId(LibraryId, version, revisionId),
            LibraryId = LibraryId,
            Version = version,
            ScanRunId = $"scan-{version}",
            DocumentId = DocumentId,
            DocumentRevisionId = revisionId,
            TaxonomyVersion = TaxonomyVersion,
            Primary = new SubjectSelection
                          {
                              SubjectId = SubjectId,
                              Confidence = 0.94f,
                              Evidence = ["pump calibration"]
                          },
            NeedsReview = false,
            Provenance = ClassifierProvenance()
        };

    private static PageRecord Page(string version, string revisionId) => new()
        {
            Id = $"{LibraryId}/{version}/page",
            LibraryId = LibraryId,
            Version = version,
            Url = $"{SourceUri}#section-0001",
            Title = "Pump Manual",
            Category = DocCategory.HowTo,
            RawContent = "Stage 7 pump calibration marker",
            FetchedAt = RecordedAt,
            ContentHash = "page-hash",
            DocumentSource = Provenance(revisionId),
            SubjectIds = [SubjectId],
            SubjectTaxonomyVersion = TaxonomyVersion
        };

    private static DocChunk Chunk(string version, string revisionId) => new()
        {
            Id = $"{LibraryId}/{version}/chunk",
            LibraryId = LibraryId,
            Version = version,
            PageUrl = $"{SourceUri}#section-0001",
            PageTitle = "Pump Manual",
            Category = DocCategory.HowTo,
            Content = "Stage 7 pump calibration marker",
            TokenCount = 6,
            Embedding = [1.0f, 0.0f],
            DocumentSource = Provenance(revisionId),
            SubjectIds = [SubjectId],
            SubjectTaxonomyVersion = TaxonomyVersion
        };

    private static DocumentProvenance Provenance(string revisionId) => new()
        {
            DocumentId = DocumentId,
            RevisionId = revisionId,
            SourceUri = SourceUri,
            RelativePath = RelativePath,
            PageStart = 3,
            PageEnd = 3,
            Heading = "Pump calibration"
        };

    private static SubjectClassifierProvenance ClassifierProvenance() => new()
        {
            Backend = "stage7",
            ModelId = "stage7-model",
            PromptVersion = "subject-v1",
            GeneratedAtUtc = RecordedAt
        };

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static readonly byte[] SharedOriginalBytes = "shared original manual bytes"u8.ToArray();
    private static readonly byte[] FirstExtractionBytes = "{\"markdown\":\"unique first extraction\"}"u8.ToArray();
    private static readonly DateTime RecordedAt = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string FirstRevisionId =
        SourceDocumentRepository.MakeRevisionId(LibraryId, FirstVersion, DocumentId);
    private static readonly string SecondRevisionId =
        SourceDocumentRepository.MakeRevisionId(LibraryId, SecondVersion, DocumentId);
    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string LibraryId = "stage7-delete-library";
    private const string FirstVersion = "2026-08-03";
    private const string SecondVersion = "2026-08-04";
    private const string DocumentId = "stable-document-id";
    private const string RelativePath = "manuals/manual.pdf";
    private const string SourceUri = "saddlerag://library/stage7-delete-library/documents/stable-document-id";
    private const string TaxonomyVersion = "taxonomy-stage7";
    private const string SubjectId = "hydraulics";
    private const string RootPath = "C:\\private\\owned-manuals";
}
