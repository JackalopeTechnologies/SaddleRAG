// DirectoryRenameLifecycleIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class DirectoryRenameLifecycleIntegrationTests : IAsyncLifetime
{
    private string mDatabaseName = string.Empty;
    private SaddleRagDbContext mContext = new(Options.Create(new SaddleRagDbSettings()));
    private LibraryRepository mLibraries = null!;
    private SourceDocumentRepository mSources = null!;
    private PageRepository mPages = null!;
    private ChunkRepository mChunks = null!;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-stage7-rename-{Guid.NewGuid():N}";
        mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings
                                                            {
                                                                ConnectionString = TestConnectionString,
                                                                DatabaseName = mDatabaseName
                                                            }));
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        mLibraries = new LibraryRepository(mContext);
        mSources = new SourceDocumentRepository(mContext);
        mPages = new PageRepository(mContext);
        mChunks = new ChunkRepository(mContext);
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
    }

    [Fact]
    public async Task RenameLibraryPreservesDocumentIdentityArtifactsSubjectsAndCitationMeaning()
    {
        await SeedDirectoryVersionAsync(OldLibraryId, Version);
        string oldRevisionId = RevisionId(OldLibraryId, Version);
        string newRevisionId = RevisionId(NewLibraryId, Version);

        RenameLibraryResponse response = await mLibraries.RenameAsync(OldLibraryId,
                                                                       NewLibraryId,
                                                                       TestContext.Current.CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, response.Outcome);
        Assert.Null(await mLibraries.GetLibraryAsync(OldLibraryId,
                                                      TestContext.Current.CancellationToken));
        Assert.NotNull(await mLibraries.GetLibraryAsync(NewLibraryId,
                                                         TestContext.Current.CancellationToken));

        DirectoryLibraryDefinition definition = Assert.IsType<DirectoryLibraryDefinition>(
            await mSources.GetDirectoryDefinitionAsync(NewLibraryId,
                                                        TestContext.Current.CancellationToken));
        Assert.Equal(RootPath, definition.RootPath);
        Assert.Equal(DirectoryLibraryBindingStatus.Bound, definition.BindingStatus);
        Assert.Null(await mSources.GetDirectoryDefinitionAsync(OldLibraryId,
                                                                TestContext.Current.CancellationToken));

        SourceDocumentRecord source = Assert.IsType<SourceDocumentRecord>(
            await mSources.GetDocumentAsync(DocumentId, TestContext.Current.CancellationToken));
        Assert.Equal(DocumentId, source.Id);
        Assert.Equal(NewLibraryId, source.LibraryId);
        Assert.Equal(RelativePath, source.NormalizedRelativePath);
        Assert.Equal(SourceUri(NewLibraryId), source.SourceUri);
        Assert.Empty(await mContext.SourceDocuments.Find(item => item.LibraryId == OldLibraryId)
                                                   .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Null(await mSources.GetRevisionAsync(oldRevisionId,
                                                     TestContext.Current.CancellationToken));
        DocumentRevisionRecord revision = Assert.IsType<DocumentRevisionRecord>(
            await mSources.GetRevisionAsync(newRevisionId,
                                             TestContext.Current.CancellationToken));
        Assert.Equal(DocumentId, revision.DocumentId);
        Assert.Equal(NewLibraryId, revision.LibraryId);
        Assert.Equal(Version, revision.Version);
        Assert.Equal(Hash(OriginalBytes), revision.OriginalArtifactHash);
        Assert.Equal(Hash(ExtractionBytes), revision.ExtractionArtifactHash);
        Assert.Equivalent(ExtractionProvenance(), revision.ExtractionProvenance, strict: true);
        Assert.Equal(OriginalBytes, await ReadArtifactAsync(revision.OriginalArtifactHash));
        Assert.Equal(ExtractionBytes, await ReadArtifactAsync(revision.ExtractionArtifactHash!));

        SubjectCatalogRecord catalog = Assert.Single(await mContext.SubjectCatalogs
                                                                     .Find(item => item.LibraryId == NewLibraryId)
                                                                     .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SubjectCatalogRepository.MakeId(NewLibraryId, TaxonomyVersion), catalog.Id);
        Assert.Equivalent(Concepts, catalog.Concepts, strict: true);
        Assert.Empty(await mContext.SubjectCatalogs.Find(item => item.LibraryId == OldLibraryId)
                                                  .ToListAsync(TestContext.Current.CancellationToken));

        SubjectAssignmentRecord assignment = Assert.Single(await mContext.SubjectAssignments
                                                                            .Find(item =>
                                                                                item.LibraryId == NewLibraryId)
                                                                            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SubjectAssignmentRepository.MakeId(NewLibraryId, Version, newRevisionId), assignment.Id);
        Assert.Equal(DocumentId, assignment.DocumentId);
        Assert.Equal(newRevisionId, assignment.DocumentRevisionId);
        Assert.Equal(SubjectId, assignment.Primary.SubjectId);
        Assert.Equal(TaxonomyVersion, assignment.TaxonomyVersion);

        PageRecord page = Assert.Single(await mPages.GetPagesAsync(NewLibraryId,
                                                                   Version,
                                                                   TestContext.Current.CancellationToken));
        DocChunk chunk = Assert.Single(await mChunks.GetChunksAsync(NewLibraryId,
                                                                    Version,
                                                                    TestContext.Current.CancellationToken));
        AssertCitation(page.DocumentSource, NewLibraryId, newRevisionId);
        AssertCitation(chunk.DocumentSource, NewLibraryId, newRevisionId);
        Assert.Equal($"{SourceUri(NewLibraryId)}#section-0001", page.Url);
        Assert.Equal($"{SourceUri(NewLibraryId)}#section-0001", chunk.PageUrl);
        Assert.Equal([SubjectId], page.SubjectIds);
        Assert.Equal([SubjectId], chunk.SubjectIds);
        Assert.Equal(TaxonomyVersion, page.SubjectTaxonomyVersion);
        Assert.Equal(TaxonomyVersion, chunk.SubjectTaxonomyVersion);
        Assert.Empty(await mPages.GetPagesAsync(OldLibraryId,
                                                 Version,
                                                 TestContext.Current.CancellationToken));
        Assert.Empty(await mChunks.GetChunksAsync(OldLibraryId,
                                                   Version,
                                                   TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenameVersionPreservesStableDocumentAndArtifactReferencesWhileRemappingRevisionLinks()
    {
        await SeedDirectoryVersionAsync(OldLibraryId, OldVersion);
        string oldRevisionId = RevisionId(OldLibraryId, OldVersion);
        string newRevisionId = RevisionId(OldLibraryId, NewVersion);

        RenameLibraryResponse response = await mLibraries.RenameVersionAsync(OldLibraryId,
                                                                              OldVersion,
                                                                              NewVersion,
                                                                              TestContext.Current.CancellationToken);

        Assert.Equal(RenameLibraryOutcome.Renamed, response.Outcome);
        LibraryRecord library = Assert.IsType<LibraryRecord>(await mLibraries.GetLibraryAsync(
                                                                  OldLibraryId,
                                                                  TestContext.Current.CancellationToken));
        Assert.Equal(NewVersion, library.CurrentVersion);
        Assert.Contains(NewVersion, library.AllVersions);
        Assert.DoesNotContain(OldVersion, library.AllVersions);

        SourceDocumentRecord source = Assert.IsType<SourceDocumentRecord>(
            await mSources.GetDocumentAsync(DocumentId, TestContext.Current.CancellationToken));
        Assert.Equal(DocumentId, source.Id);
        Assert.Equal(OldLibraryId, source.LibraryId);
        Assert.Equal(SourceUri(OldLibraryId), source.SourceUri);
        DirectoryLibraryDefinition definition = Assert.IsType<DirectoryLibraryDefinition>(
            await mSources.GetDirectoryDefinitionAsync(OldLibraryId,
                                                        TestContext.Current.CancellationToken));
        Assert.Equal(RootPath, definition.RootPath);
        Assert.Equal(DirectoryLibraryBindingStatus.Bound, definition.BindingStatus);

        Assert.Null(await mSources.GetRevisionAsync(oldRevisionId,
                                                     TestContext.Current.CancellationToken));
        DocumentRevisionRecord revision = Assert.IsType<DocumentRevisionRecord>(
            await mSources.GetRevisionAsync(newRevisionId,
                                             TestContext.Current.CancellationToken));
        Assert.Equal(DocumentId, revision.DocumentId);
        Assert.Equal(NewVersion, revision.Version);
        Assert.Equal(Hash(OriginalBytes), revision.OriginalArtifactHash);
        Assert.Equal(Hash(ExtractionBytes), revision.ExtractionArtifactHash);
        Assert.Equal(OriginalBytes, await ReadArtifactAsync(revision.OriginalArtifactHash));
        Assert.Equal(ExtractionBytes, await ReadArtifactAsync(revision.ExtractionArtifactHash!));

        SubjectCatalogRecord catalog = Assert.Single(await mContext.SubjectCatalogs
                                                                     .Find(item => item.LibraryId == OldLibraryId)
                                                                     .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SubjectCatalogRepository.MakeId(OldLibraryId, TaxonomyVersion), catalog.Id);
        SubjectAssignmentRecord assignment = Assert.Single(await mContext.SubjectAssignments
                                                                            .Find(item =>
                                                                                item.LibraryId == OldLibraryId &&
                                                                                item.Version == NewVersion)
                                                                            .ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(SubjectAssignmentRepository.MakeId(OldLibraryId, NewVersion, newRevisionId), assignment.Id);
        Assert.Equal(DocumentId, assignment.DocumentId);
        Assert.Equal(newRevisionId, assignment.DocumentRevisionId);
        Assert.Empty(await mContext.SubjectAssignments.Find(item => item.LibraryId == OldLibraryId &&
                                                                    item.Version == OldVersion)
                                                      .ToListAsync(TestContext.Current.CancellationToken));

        PageRecord page = Assert.Single(await mPages.GetPagesAsync(OldLibraryId,
                                                                   NewVersion,
                                                                   TestContext.Current.CancellationToken));
        DocChunk chunk = Assert.Single(await mChunks.GetChunksAsync(OldLibraryId,
                                                                    NewVersion,
                                                                    TestContext.Current.CancellationToken));
        AssertCitation(page.DocumentSource, OldLibraryId, newRevisionId);
        AssertCitation(chunk.DocumentSource, OldLibraryId, newRevisionId);
        Assert.Equal([SubjectId], page.SubjectIds);
        Assert.Equal([SubjectId], chunk.SubjectIds);
        Assert.Empty(await mPages.GetPagesAsync(OldLibraryId,
                                                 OldVersion,
                                                 TestContext.Current.CancellationToken));
        Assert.Empty(await mChunks.GetChunksAsync(OldLibraryId,
                                                   OldVersion,
                                                   TestContext.Current.CancellationToken));
    }

    private async Task SeedDirectoryVersionAsync(string libraryId, string version)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await mLibraries.UpsertLibraryAsync(new LibraryRecord
                                                {
                                                    Id = libraryId,
                                                    Name = "Owned manuals",
                                                    Hint = "stage 7 rename",
                                                    CurrentVersion = version,
                                                    AllVersions = [version]
                                                },
                                            ct);
        await mLibraries.UpsertVersionAsync(new LibraryVersionRecord
                                                {
                                                    Id = $"{libraryId}/{version}",
                                                    LibraryId = libraryId,
                                                    Version = version,
                                                    ScrapedAt = RecordedAt,
                                                    PageCount = 1,
                                                    ChunkCount = 1,
                                                    EmbeddingProviderId = "stage7",
                                                    EmbeddingModelName = "stage7",
                                                    EmbeddingDimensions = 2,
                                                    PublicationState = VersionPublicationState.Published
                                                },
                                            ct);
        await mSources.UpsertDirectoryDefinitionAsync(new DirectoryLibraryDefinition
                                                          {
                                                              Id = libraryId,
                                                              RootPath = RootPath,
                                                              Recursive = true,
                                                              AllowedExtensions = [".pdf"],
                                                              ExclusionPatterns = ["**/temp/**"],
                                                              BindingStatus = DirectoryLibraryBindingStatus.Bound,
                                                              RegisteredAtUtc = RecordedAt,
                                                              LastPublishedAtUtc = RecordedAt,
                                                              LastPublishedVersion = version
                                                          },
                                                      ct);
        await mSources.GetOrCreateDocumentAsync(new SourceDocumentRecord
                                                    {
                                                        Id = DocumentId,
                                                        LibraryId = libraryId,
                                                        NormalizedRelativePath = RelativePath,
                                                        DisplayRelativePath = RelativePath,
                                                        DisplayName = "manual.pdf",
                                                        SourceUri = SourceUri(libraryId),
                                                        MediaType = "application/pdf",
                                                        FirstSeenVersion = version,
                                                        LastSeenVersion = version,
                                                        CreatedAtUtc = RecordedAt,
                                                        UpdatedAtUtc = RecordedAt
                                                    },
                                                ct);
        string revisionId = RevisionId(libraryId, version);
        var revision = new DocumentRevisionRecord
                           {
                               Id = revisionId,
                               DocumentId = DocumentId,
                               LibraryId = libraryId,
                               Version = version,
                               ScanRunId = "stage7-rename-scan",
                               State = DocumentRevisionState.Published,
                               SourceModifiedAtUtc = RecordedAt,
                               AcquiredAtUtc = RecordedAt,
                               OriginalArtifactHash = Hash(OriginalBytes),
                               OriginalByteLength = OriginalBytes.LongLength,
                               OriginalMediaType = "application/pdf",
                               ExtractionArtifactHash = Hash(ExtractionBytes),
                               ExtractionByteLength = ExtractionBytes.LongLength,
                               ExtractionMediaType = "application/json",
                               ExtractionProvenance = ExtractionProvenance(),
                               PublishedAtUtc = RecordedAt
                           };
        await using (var original = new MemoryStream(OriginalBytes, writable: false))
        await using (var extraction = new MemoryStream(ExtractionBytes, writable: false))
            await mSources.PersistRevisionAsync(revision, original, extraction, ct);

        var catalog = new SubjectCatalogRecord
                          {
                              Id = SubjectCatalogRepository.MakeId(libraryId, TaxonomyVersion),
                              LibraryId = libraryId,
                              Revision = 1,
                              TaxonomyVersion = TaxonomyVersion,
                              Concepts = Concepts,
                              Provenance = ClassifierProvenance(),
                              CreatedAtUtc = RecordedAt
                          };
        await new SubjectCatalogRepository(mContext).InsertRevisionAsync(catalog, ct);
        var assignment = new SubjectAssignmentRecord
                             {
                                 Id = SubjectAssignmentRepository.MakeId(libraryId, version, revisionId),
                                 LibraryId = libraryId,
                                 Version = version,
                                 ScanRunId = "stage7-rename-scan",
                                 DocumentId = DocumentId,
                                 DocumentRevisionId = revisionId,
                                 TaxonomyVersion = TaxonomyVersion,
                                 Primary = new SubjectSelection
                                               {
                                                   SubjectId = SubjectId,
                                                   Confidence = 0.91f,
                                                   Evidence = ["pump marker"]
                                               },
                                 NeedsReview = false,
                                 Provenance = ClassifierProvenance()
                             };
        await new SubjectAssignmentRepository(mContext).PersistAsync(assignment, ct);

        DocumentProvenance provenance = Citation(libraryId, revisionId);
        await mPages.UpsertPageAsync(new PageRecord
                                         {
                                             // Opaque document page ids deliberately expose segment-based rename bugs.
                                             Id = $"document-page-{Guid.NewGuid():N}",
                                             LibraryId = libraryId,
                                             Version = version,
                                             Url = $"{SourceUri(libraryId)}#section-0001",
                                             Title = "Pump Manual",
                                             Category = DocCategory.HowTo,
                                             RawContent = SearchMarker,
                                             FetchedAt = RecordedAt,
                                             ContentHash = "page-content-hash",
                                             DocumentSource = provenance,
                                             SubjectIds = [SubjectId],
                                             SubjectTaxonomyVersion = TaxonomyVersion
                                         },
                                     ct);
        await mChunks.InsertChunksAsync([
                                            new DocChunk
                                                {
                                                    Id = $"{libraryId}/{version}/chunk-1",
                                                    LibraryId = libraryId,
                                                    Version = version,
                                                    PageUrl = $"{SourceUri(libraryId)}#section-0001",
                                                    PageTitle = "Pump Manual",
                                                    Category = DocCategory.HowTo,
                                                    Content = SearchMarker,
                                                    TokenCount = 5,
                                                    Embedding = [1.0f, 0.0f],
                                                    DocumentSource = provenance,
                                                    SubjectIds = [SubjectId],
                                                    SubjectTaxonomyVersion = TaxonomyVersion
                                                }
                                        ],
                                        ct);
    }

    private async Task<byte[]> ReadArtifactAsync(string hash)
    {
        await using Stream stream = await mSources.OpenArtifactAsync(hash,
                                                                      TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }

    private static void AssertCitation(DocumentProvenance? source,
                                       string libraryId,
                                       string revisionId)
    {
        Assert.NotNull(source);
        Assert.Equal(DocumentId, source.DocumentId);
        Assert.Equal(revisionId, source.RevisionId);
        Assert.Equal(SourceUri(libraryId), source.SourceUri);
        Assert.Equal(RelativePath, source.RelativePath);
        Assert.Equal(7, source.PageStart);
        Assert.Equal(7, source.PageEnd);
        Assert.Equal("Hydraulic setup", source.Heading);
    }

    private static DocumentProvenance Citation(string libraryId, string revisionId) => new()
        {
            DocumentId = DocumentId,
            RevisionId = revisionId,
            SourceUri = SourceUri(libraryId),
            RelativePath = RelativePath,
            PageStart = 7,
            PageEnd = 7,
            Heading = "Hydraulic setup"
        };

    private static DocumentExtractionProvenance ExtractionProvenance() => new()
        {
            ExtractorName = "docling",
            ExtractorVersion = "2.50.0",
            ConfigurationHash = "stage7-config",
            UsedOcr = true,
            QualityScore = 0.97,
            Warnings = ["fixture warning"]
        };

    private static SubjectClassifierProvenance ClassifierProvenance() => new()
        {
            Backend = "stage7",
            ModelId = "stage7-model",
            PromptVersion = "subject-v1",
            GeneratedAtUtc = RecordedAt
        };

    private static string RevisionId(string libraryId, string version) =>
        SourceDocumentRepository.MakeRevisionId(libraryId, version, DocumentId);

    private static string SourceUri(string libraryId) =>
        $"saddlerag://library/{libraryId}/documents/{DocumentId}";

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static readonly IReadOnlyList<SubjectConcept> Concepts =
    [
        new SubjectConcept
            {
                Id = SubjectId,
                Label = "Hydraulics",
                Aliases = ["fluid power"],
                Description = "Hydraulic service procedures"
            }
    ];
    private static readonly byte[] OriginalBytes = "exact original manual bytes"u8.ToArray();
    private static readonly byte[] ExtractionBytes = "{\"markdown\":\"exact extraction\"}"u8.ToArray();
    private static readonly DateTime RecordedAt = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string OldLibraryId = "stage7-old-library";
    private const string NewLibraryId = "stage7-new-library";
    private const string Version = "2026-08-04";
    private const string OldVersion = "current";
    private const string NewVersion = "2026-08-04";
    private const string DocumentId = "stable-document-id";
    private const string RelativePath = "manuals/pump-manual.pdf";
    private const string RootPath = "C:\\private\\owned-manuals";
    private const string TaxonomyVersion = "taxonomy-stage7";
    private const string SubjectId = "hydraulics";
    private const string SearchMarker = "Stage 7 hydraulic calibration marker";
}
