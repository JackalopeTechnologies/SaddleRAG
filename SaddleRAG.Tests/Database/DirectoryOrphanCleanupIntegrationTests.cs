// Stage 7 RED integration contract for directory-document orphan cleanup.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class DirectoryOrphanCleanupIntegrationTests : IAsyncLifetime
{
    private string mDatabaseName = string.Empty;
    private SaddleRagDbContext mContext = new(Options.Create(new SaddleRagDbSettings()));
    private RepositoryFactory mFactory = null!;
    private ILibraryRepository mLibraries = null!;
    private IPageRepository mPages = null!;
    private ISourceDocumentRepository mSources = null!;
    private ISubjectCatalogRepository mCatalogs = null!;
    private ISubjectAssignmentRepository mAssignments = null!;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-stage7-orphans-{Guid.NewGuid():N}";
        var settings = Options.Create(new SaddleRagDbSettings
                                          {
                                              ConnectionString = TestConnectionString,
                                              DatabaseName = mDatabaseName
                                          });
        var contextFactory = new SaddleRagDbContextFactory(settings);
        mContext = contextFactory.GetDefault();
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        mFactory = new RepositoryFactory(contextFactory);
        mLibraries = mFactory.GetLibraryRepository();
        mPages = mFactory.GetPageRepository();
        mSources = mFactory.GetSourceDocumentRepository();
        mCatalogs = mFactory.GetSubjectCatalogRepository();
        mAssignments = mFactory.GetSubjectAssignmentRepository();
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
    }

    [Fact]
    public async Task DryRunReportsEveryDirectoryStoreAndOnlyTheArtifactThatWouldBecomeUnreferenced()
    {
        await SeedValidAndOrphanedDocumentsAsync();

        string json = await OrphanCleanupTools.CleanupOrphans(mFactory,
                                                               NoopRunner(),
                                                               library: OrphanLibraryId,
                                                               version: Version,
                                                               dryRun: true,
                                                               profile: null,
                                                               TestContext.Current.CancellationToken);

        JsonObject byCollection = JsonNode.Parse(json)!["WouldDelete"]!["ByCollection"]!.AsObject();
        Assert.Equal(1, byCollection["DirectoryLibraries"]!.GetValue<int>());
        Assert.Equal(1, byCollection["SourceDocuments"]!.GetValue<int>());
        Assert.Equal(1, byCollection["DocumentRevisions"]!.GetValue<int>());
        Assert.Equal(1, byCollection["SubjectCatalogs"]!.GetValue<int>());
        Assert.Equal(1, byCollection["SubjectAssignments"]!.GetValue<int>());
        Assert.Equal(1, byCollection["DocumentArtifacts"]!.GetValue<int>());
        Assert.DoesNotContain(OrphanRootPath, json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await mSources.GetRevisionAsync(OrphanRevisionId,
                                                        TestContext.Current.CancellationToken));
        Assert.NotNull(await mCatalogs.GetAsync(OrphanLibraryId,
                                                 TaxonomyVersion,
                                                 TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyRemovesOrphanDirectoryMetadataSubjectsAndUniqueArtifactButKeepsSharedBlob()
    {
        await SeedValidAndOrphanedDocumentsAsync();

        BackgroundJobRecord? completedJob = null;
        string queuedJson = await OrphanCleanupTools.CleanupOrphans(mFactory,
                                                                     InlineRunner(record => completedJob = record),
                                                                     library: OrphanLibraryId,
                                                                     version: Version,
                                                                     dryRun: false,
                                                                     profile: null,
                                                                     TestContext.Current.CancellationToken);

        Assert.Contains("\"Status\": \"Queued\"", queuedJson, StringComparison.Ordinal);
        BackgroundJobRecord job = Assert.IsType<BackgroundJobRecord>(completedJob);
        string resultJson = Assert.IsType<string>(job.ResultJson);
        JsonObject result = Assert.IsType<JsonObject>(JsonNode.Parse(resultJson));
        JsonObject deleted = Assert.IsType<JsonObject>(result["Deleted"]);
        Assert.Equal(1, deleted["DirectoryLibraries"]!.GetValue<long>());
        Assert.Equal(1, deleted["SourceDocuments"]!.GetValue<long>());
        Assert.Equal(1, deleted["DocumentRevisions"]!.GetValue<long>());
        Assert.Equal(1, deleted["SubjectCatalogs"]!.GetValue<long>());
        Assert.Equal(1, deleted["SubjectAssignments"]!.GetValue<long>());
        Assert.Equal(1, deleted["DocumentArtifacts"]!.GetValue<long>());

        Assert.Null(await mSources.GetDirectoryDefinitionAsync(OrphanLibraryId,
                                                                TestContext.Current.CancellationToken));
        Assert.Null(await mSources.GetDocumentAsync(OrphanDocumentId,
                                                     TestContext.Current.CancellationToken));
        Assert.Null(await mSources.GetRevisionAsync(OrphanRevisionId,
                                                     TestContext.Current.CancellationToken));
        Assert.Empty(await mAssignments.GetByDocumentRevisionIdsAsync([OrphanRevisionId],
                                                                       TestContext.Current.CancellationToken));
        Assert.Null(await mCatalogs.GetAsync(OrphanLibraryId,
                                              TaxonomyVersion,
                                              TestContext.Current.CancellationToken));
        Assert.Empty(await mPages.GetPagesAsync(OrphanLibraryId,
                                                 Version,
                                                 TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mSources.OpenArtifactAsync(
                                                                 Hash(UniqueExtractionBytes),
                                                                 TestContext.Current.CancellationToken));

        Assert.NotNull(await mLibraries.GetLibraryAsync(ValidLibraryId,
                                                         TestContext.Current.CancellationToken));
        Assert.NotNull(await mSources.GetRevisionAsync(ValidRevisionId,
                                                        TestContext.Current.CancellationToken));
        Assert.Equal(SharedOriginalBytes, await ReadArtifactAsync(Hash(SharedOriginalBytes)));
    }

    [Fact]
    public async Task ApplyRejectsBusyDirectoryLifecycleBeforeDeletingAnyOrphanData()
    {
        await SeedValidAndOrphanedDocumentsAsync();
        CancellationToken ct = TestContext.Current.CancellationToken;
        DirectoryLibraryDefinition definition = Assert.IsType<DirectoryLibraryDefinition>(
            await mSources.GetDirectoryDefinitionAsync(OrphanLibraryId, ct));
        IDirectoryPublicationLease? blockingLease =
            await mSources.TryAcquireDirectoryPublicationLeaseAsync(
                OrphanLibraryId,
                definition.RegistrationRevision,
                definition.RegistrationIncarnationId,
                "active-directory-scan",
                definition.LastPublishedVersion,
                ct);
        Assert.NotNull(blockingLease);

        await using(blockingLease)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => OrphanCleanupTools.CleanupOrphans(
                mFactory,
                InlineRunner(_ => { }),
                library: OrphanLibraryId,
                version: Version,
                dryRun: false,
                profile: null,
                ct));

            Assert.NotNull(await mSources.GetDirectoryDefinitionAsync(OrphanLibraryId, ct));
            Assert.NotNull(await mSources.GetDocumentAsync(OrphanDocumentId, ct));
            Assert.NotNull(await mSources.GetRevisionAsync(OrphanRevisionId, ct));
            Assert.NotNull(await mCatalogs.GetAsync(OrphanLibraryId, TaxonomyVersion, ct));
            Assert.NotEmpty(await mPages.GetPagesAsync(OrphanLibraryId, Version, ct));
        }
    }

    private async Task SeedValidAndOrphanedDocumentsAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await mLibraries.UpsertLibraryAsync(new LibraryRecord
                                                {
                                                    Id = ValidLibraryId,
                                                    Name = "valid",
                                                    Hint = "valid parent",
                                                    CurrentVersion = Version,
                                                    AllVersions = [Version]
                                                },
                                            ct);
        await mLibraries.UpsertVersionAsync(new LibraryVersionRecord
                                                {
                                                    Id = $"{ValidLibraryId}/{Version}",
                                                    LibraryId = ValidLibraryId,
                                                    Version = Version,
                                                    ScrapedAt = RecordedAt,
                                                    PageCount = 0,
                                                    ChunkCount = 0,
                                                    EmbeddingProviderId = "stage7",
                                                    EmbeddingModelName = "stage7",
                                                    EmbeddingDimensions = 2,
                                                    PublicationState = VersionPublicationState.Published
                                                },
                                            ct);
        await mSources.GetOrCreateDocumentAsync(Document(ValidLibraryId,
                                                          ValidDocumentId,
                                                          "valid.pdf"),
                                                ct);
        await PersistAsync(Revision(ValidLibraryId,
                                    ValidDocumentId,
                                    ValidRevisionId,
                                    SharedOriginalBytes,
                                    extraction: null,
                                    DocumentRevisionState.Published),
                           SharedOriginalBytes,
                           extraction: null);

        await mSources.UpsertDirectoryDefinitionAsync(new DirectoryLibraryDefinition
                                                          {
                                                              Id = OrphanLibraryId,
                                                              RootPath = OrphanRootPath,
                                                              Recursive = true,
                                                              AllowedExtensions = [".pdf"],
                                                              ExclusionPatterns = [],
                                                              BindingStatus = DirectoryLibraryBindingStatus.Bound,
                                                              RegisteredAtUtc = RecordedAt
                                                          },
                                                      ct);
        await mSources.GetOrCreateDocumentAsync(Document(OrphanLibraryId,
                                                          OrphanDocumentId,
                                                          "orphan.pdf"),
                                                ct);
        await PersistAsync(Revision(OrphanLibraryId,
                                    OrphanDocumentId,
                                    OrphanRevisionId,
                                    SharedOriginalBytes,
                                    UniqueExtractionBytes,
                                    DocumentRevisionState.Candidate),
                           SharedOriginalBytes,
                           UniqueExtractionBytes);
        await mPages.UpsertPageAsync(new PageRecord
                                         {
                                             Id = $"{OrphanLibraryId}/{Version}/page",
                                             LibraryId = OrphanLibraryId,
                                             Version = Version,
                                             Url = SourceUri(OrphanLibraryId, OrphanDocumentId),
                                             Title = "orphan",
                                             Category = DocCategory.HowTo,
                                             RawContent = "orphan marker",
                                             FetchedAt = RecordedAt,
                                             ContentHash = "orphan-hash",
                                             DocumentSource = Provenance(OrphanLibraryId,
                                                                         OrphanDocumentId,
                                                                         OrphanRevisionId)
                                         },
                                     ct);
        await mCatalogs.InsertRevisionAsync(new SubjectCatalogRecord
                                                {
                                                    Id = SubjectCatalogRepository.MakeId(OrphanLibraryId,
                                                                                         TaxonomyVersion),
                                                    LibraryId = OrphanLibraryId,
                                                    Revision = 1,
                                                    TaxonomyVersion = TaxonomyVersion,
                                                    Concepts =
                                                    [
                                                        new SubjectConcept
                                                            {
                                                                Id = SubjectId,
                                                                Label = "Orphan subject",
                                                                Description = "stage 7 orphan fixture"
                                                            }
                                                    ],
                                                    Provenance = ClassifierProvenance(),
                                                    CreatedAtUtc = RecordedAt
                                                },
                                            ct);
        await mAssignments.PersistAsync(new SubjectAssignmentRecord
                                             {
                                                 Id = SubjectAssignmentRepository.MakeId(OrphanLibraryId,
                                                                                         Version,
                                                                                         OrphanRevisionId),
                                                 LibraryId = OrphanLibraryId,
                                                 Version = Version,
                                                 ScanRunId = "orphan-scan",
                                                 DocumentId = OrphanDocumentId,
                                                 DocumentRevisionId = OrphanRevisionId,
                                                 TaxonomyVersion = TaxonomyVersion,
                                                 Primary = new SubjectSelection
                                                               {
                                                                   SubjectId = SubjectId,
                                                                   Confidence = 0.88f
                                                               },
                                                 NeedsReview = false,
                                                 Provenance = ClassifierProvenance()
                                             },
                                         ct);
    }

    private async Task PersistAsync(DocumentRevisionRecord revision,
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

    private static SourceDocumentRecord Document(string libraryId,
                                                 string documentId,
                                                 string relativePath) => new()
        {
            Id = documentId,
            LibraryId = libraryId,
            NormalizedRelativePath = relativePath,
            DisplayRelativePath = relativePath,
            DisplayName = relativePath,
            SourceUri = SourceUri(libraryId, documentId),
            MediaType = "application/pdf",
            FirstSeenVersion = Version,
            LastSeenVersion = Version,
            CreatedAtUtc = RecordedAt,
            UpdatedAtUtc = RecordedAt
        };

    private static DocumentRevisionRecord Revision(string libraryId,
                                                   string documentId,
                                                   string revisionId,
                                                   byte[] original,
                                                   byte[]? extraction,
                                                   DocumentRevisionState state) => new()
        {
            Id = revisionId,
            DocumentId = documentId,
            LibraryId = libraryId,
            Version = Version,
            ScanRunId = $"scan-{libraryId}",
            State = state,
            AcquiredAtUtc = RecordedAt,
            SourceModifiedAtUtc = RecordedAt,
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
                                                 UsedOcr = false
                                             },
            PublishedAtUtc = state == DocumentRevisionState.Published ? RecordedAt : null
        };

    private static DocumentProvenance Provenance(string libraryId,
                                                 string documentId,
                                                 string revisionId) => new()
        {
            DocumentId = documentId,
            RevisionId = revisionId,
            SourceUri = SourceUri(libraryId, documentId),
            RelativePath = "orphan.pdf",
            PageStart = 1,
            PageEnd = 1,
            Heading = "Orphan"
        };

    private static SubjectClassifierProvenance ClassifierProvenance() => new()
        {
            Backend = "stage7",
            ModelId = "stage7-model",
            PromptVersion = "subject-v1",
            GeneratedAtUtc = RecordedAt
        };

    private static IBackgroundJobRunner NoopRunner()
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>())
              .Returns("job");
        return runner;
    }

    private static IBackgroundJobRunner InlineRunner(Action<BackgroundJobRecord> onCompleted)
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>())
              .Returns(async call =>
                   {
                       BackgroundJobRecord record = Assert.IsType<BackgroundJobRecord>(call[index: 0]);
                       Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task> action =
                           Assert.IsType<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(
                               call[index: 1]);
                       await action(record, null, CancellationToken.None);
                       onCompleted(record);
                       return record.Id;
                   });
        return runner;
    }

    private static string SourceUri(string libraryId, string documentId) =>
        $"saddlerag://library/{libraryId}/documents/{documentId}";

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static readonly byte[] SharedOriginalBytes = "shared artifact across valid and orphan"u8.ToArray();
    private static readonly byte[] UniqueExtractionBytes = "orphan-only extraction"u8.ToArray();
    private static readonly DateTime RecordedAt = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string ValidRevisionId =
        SourceDocumentRepository.MakeRevisionId(ValidLibraryId, Version, ValidDocumentId);
    private static readonly string OrphanRevisionId =
        SourceDocumentRepository.MakeRevisionId(OrphanLibraryId, Version, OrphanDocumentId);
    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string ValidLibraryId = "stage7-valid-library";
    private const string OrphanLibraryId = "stage7-orphan-library";
    private const string ValidDocumentId = "stage7-valid-document";
    private const string OrphanDocumentId = "stage7-orphan-document";
    private const string Version = "2026-08-04";
    private const string TaxonomyVersion = "taxonomy-stage7";
    private const string SubjectId = "orphan-subject";
    private const string OrphanRootPath = "C:\\private\\orphaned-root";
}
