// DirectoryPackagingRoundTripIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Diagnostics;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Services;
using SaddleRAG.Mcp.Tools;
using SaddleRAG.Packaging;
using SaddleRAG.Tests.Packaging.Fixtures;

namespace SaddleRAG.Tests.Packaging;

[Trait("Category", "Integration")]
public sealed class DirectoryPackagingRoundTripIntegrationTests : IAsyncLifetime
{
    private string mDatabaseName = string.Empty;
    private string mBundlePath = string.Empty;
    private SaddleRagDbContext mContext = new(Options.Create(new SaddleRagDbSettings()));
    private RepositoryFactory mFactory = null!;
    private ILibraryRepository mLibraries = null!;
    private IJobRepository mJobs = null!;
    private ILibraryProfileRepository mProfiles = null!;
    private ILibraryIndexRepository mIndexes = null!;
    private IExcludedSymbolsRepository mExcluded = null!;
    private IDiffRepository mDiffs = null!;
    private IPageRepository mPages = null!;
    private IChunkRepository mChunks = null!;
    private IBm25ShardRepository mBm25 = null!;
    private ISourceDocumentRepository mSources = null!;
    private ISubjectCatalogRepository mCatalogs = null!;
    private ISubjectAssignmentRepository mAssignments = null!;
    private Stage7EmbeddingProvider mEmbedding = null!;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"saddlerag-stage7-package-{Guid.NewGuid():N}";
        mBundlePath = Path.Combine(Path.GetTempPath(),
                                   $"stage7-package-{Guid.NewGuid():N}.srlib.zip");
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
        mJobs = mFactory.GetJobRepository();
        mProfiles = mFactory.GetLibraryProfileRepository();
        mIndexes = mFactory.GetLibraryIndexRepository();
        mExcluded = mFactory.GetExcludedSymbolsRepository();
        mDiffs = mFactory.GetDiffRepository();
        mPages = mFactory.GetPageRepository();
        mChunks = mFactory.GetChunkRepository();
        mBm25 = mFactory.GetBm25ShardRepository();
        mSources = mFactory.GetSourceDocumentRepository();
        mCatalogs = mFactory.GetSubjectCatalogRepository();
        mAssignments = mFactory.GetSubjectAssignmentRepository();
        mEmbedding = new Stage7EmbeddingProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
        if (File.Exists(mBundlePath))
            File.Delete(mBundlePath);
    }

    [Fact]
    public async Task V2RoundTripRestoresExactArtifactsProvenanceSubjectsCitationsAndSearchableUnboundLibrary()
    {
        await SeedDirectoryLibraryAsync();
        await ExportAsync();
        await DeleteLibraryAsync(DirectoryPackagingFixtures.LibraryId);

        ImportResult result = await Importer(mChunks).ImportAsync(new ImportRequest
                                                                      {
                                                                          BundlePath = mBundlePath
                                                                      },
                                                                  progress: null,
                                                                  TestContext.Current.CancellationToken);

        Assert.Contains(DirectoryPackagingFixtures.Version, result.VersionsImported);
        Assert.Empty(result.PartialFailures);
        LibraryRecord library = Assert.IsType<LibraryRecord>(await mLibraries.GetLibraryAsync(
                                                                  DirectoryPackagingFixtures.LibraryId,
                                                                  TestContext.Current.CancellationToken));
        Assert.Equal(DirectoryPackagingFixtures.Version, library.CurrentVersion);
        DirectoryLibraryDefinition definition = Assert.IsType<DirectoryLibraryDefinition>(
            await mSources.GetDirectoryDefinitionAsync(DirectoryPackagingFixtures.LibraryId,
                                                        TestContext.Current.CancellationToken));
        Assert.Equal(DirectoryLibraryBindingStatus.Unbound, definition.BindingStatus);
        Assert.True(string.IsNullOrEmpty(definition.RootPath));
        Assert.Equal(DirectoryPackagingFixtures.DirectoryDefinition().Recursive, definition.Recursive);
        Assert.Equal(DirectoryPackagingFixtures.DirectoryDefinition().AllowedExtensions,
                     definition.AllowedExtensions);
        Assert.Equal(DirectoryPackagingFixtures.DirectoryDefinition().ExclusionPatterns,
                     definition.ExclusionPatterns);

        SourceDocumentRecord source = Assert.IsType<SourceDocumentRecord>(
            await mSources.GetDocumentAsync(DirectoryPackagingFixtures.DocumentId,
                                             TestContext.Current.CancellationToken));
        Assert.Equal(DirectoryPackagingFixtures.Source(), source);
        DocumentRevisionRecord revision = Assert.IsType<DocumentRevisionRecord>(
            await mSources.GetRevisionAsync(DirectoryPackagingFixtures.RevisionId(),
                                             TestContext.Current.CancellationToken));
        Assert.Equivalent(DirectoryPackagingFixtures.Revision(), revision, strict: true);
        Assert.Equivalent(DirectoryPackagingFixtures.ExtractionProvenance(),
                          revision.ExtractionProvenance,
                          strict: true);
        Assert.Equal(DirectoryPackagingFixtures.OriginalBytes,
                     await ReadArtifactAsync(revision.OriginalArtifactHash));
        Assert.Equal(DirectoryPackagingFixtures.ExtractionBytes,
                     await ReadArtifactAsync(revision.ExtractionArtifactHash!));

        SubjectCatalogRecord catalog = Assert.IsType<SubjectCatalogRecord>(await mCatalogs.GetAsync(
                                                                               DirectoryPackagingFixtures.LibraryId,
                                                                               DirectoryPackagingFixtures
                                                                                   .TaxonomyVersion,
                                                                               TestContext.Current.CancellationToken));
        Assert.Equivalent(DirectoryPackagingFixtures.Catalog(), catalog, strict: true);
        SubjectAssignmentRecord assignment = Assert.Single(
            await mAssignments.GetByDocumentRevisionIdsAsync([DirectoryPackagingFixtures.RevisionId()],
                                                              TestContext.Current.CancellationToken));
        Assert.Equivalent(DirectoryPackagingFixtures.Assignment(), assignment, strict: true);

        PageRecord page = Assert.Single(await mPages.GetPagesAsync(DirectoryPackagingFixtures.LibraryId,
                                                                   DirectoryPackagingFixtures.Version,
                                                                   TestContext.Current.CancellationToken));
        DocChunk chunk = Assert.Single(await mChunks.GetChunksAsync(DirectoryPackagingFixtures.LibraryId,
                                                                    DirectoryPackagingFixtures.Version,
                                                                    TestContext.Current.CancellationToken));
        Assert.Equal(DirectoryPackagingFixtures.Provenance(), page.DocumentSource);
        Assert.Equal(DirectoryPackagingFixtures.Provenance(), chunk.DocumentSource);
        Assert.Equal([DirectoryPackagingFixtures.SubjectId,
                      DirectoryPackagingFixtures.SecondarySubjectId],
                     page.SubjectIds);
        Assert.Equal(page.SubjectIds, chunk.SubjectIds);

        var vector = new InMemoryBruteForceVectorSearch();
        await vector.IndexChunksAsync(profile: null,
                                      DirectoryPackagingFixtures.LibraryId,
                                      DirectoryPackagingFixtures.Version,
                                      [chunk],
                                      TestContext.Current.CancellationToken);
        string searchJson = await SearchTools.SearchDocs(vector,
                                                          mEmbedding,
                                                          Substitute.For<IReRanker>(),
                                                          mFactory,
                                                          Options.Create(new RankingSettings
                                                                             {
                                                                                 ReRankerStrategy =
                                                                                     ReRankerStrategy.Off
                                                                             }),
                                                          Substitute.For<IQueryMetrics>(),
                                                          NullLogger<SearchTools.SearchToolsLog>.Instance,
                                                          DirectoryPackagingFixtures.SearchMarker,
                                                          library: DirectoryPackagingFixtures.LibraryId,
                                                          category: null,
                                                          subject: null,
                                                          version: null,
                                                          maxResults: 5,
                                                          profile: null,
                                                          TestContext.Current.CancellationToken);
        JsonObject match = Assert.Single(JsonNode.Parse(searchJson)!["Results"]!.AsArray())!.AsObject();
        JsonObject citation = match["DocumentSource"]!.AsObject();
        Assert.Equal(DirectoryPackagingFixtures.RelativePath,
                     citation["RelativePath"]!.GetValue<string>());
        Assert.Equal(12, citation["PageStart"]!.GetValue<int>());
        Assert.Equal(13, citation["PageEnd"]!.GetValue<int>());
        Assert.Equal("Hydraulic pump calibration", citation["Heading"]!.GetValue<string>());
        Assert.NotNull(match["Subjects"]);
        Assert.DoesNotContain(DirectoryPackagingFixtures.RootPath,
                              searchJson,
                              StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedImportRollbackRemovesCreatedMetadataAndUniqueArtifactButPreservesSharedBlob()
    {
        await SeedSharedReceiverReferenceAsync();
        await SeedDirectoryLibraryAsync();
        await ExportAsync();
        await DeleteLibraryAsync(DirectoryPackagingFixtures.LibraryId);
        Assert.Equal(DirectoryPackagingFixtures.OriginalBytes,
                     await ReadArtifactAsync(DirectoryPackagingFixtures.Hash(
                                                 DirectoryPackagingFixtures.OriginalBytes)));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mSources.OpenArtifactAsync(
                                                                 DirectoryPackagingFixtures.Hash(
                                                                     DirectoryPackagingFixtures.ExtractionBytes),
                                                                 TestContext.Current.CancellationToken));

        var failingChunks = Substitute.For<IChunkRepository>();
        failingChunks.InsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>())
                     .Returns(_ => Task.FromException(new InvalidOperationException(
                                         "stage7 forced failure after document artifacts")));
        failingChunks.DeleteChunksAsync(Arg.Any<string>(),
                                        Arg.Any<string>(),
                                        Arg.Any<CancellationToken>())
                     .Returns(0L);

        ImportResult result = await Importer(failingChunks).ImportAsync(new ImportRequest
                                                                            {
                                                                                BundlePath = mBundlePath
                                                                            },
                                                                        progress: null,
                                                                        TestContext.Current.CancellationToken);

        Assert.Empty(result.VersionsImported);
        Assert.Single(result.PartialFailures);
        await failingChunks.Received(requiredNumberOfCalls: 1)
                           .InsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(),
                                              Arg.Any<CancellationToken>());
        Assert.Null(await mLibraries.GetLibraryAsync(DirectoryPackagingFixtures.LibraryId,
                                                      TestContext.Current.CancellationToken));
        Assert.Null(await mSources.GetDirectoryDefinitionAsync(DirectoryPackagingFixtures.LibraryId,
                                                                TestContext.Current.CancellationToken));
        Assert.Null(await mSources.GetDocumentAsync(DirectoryPackagingFixtures.DocumentId,
                                                     TestContext.Current.CancellationToken));
        Assert.Null(await mSources.GetRevisionAsync(DirectoryPackagingFixtures.RevisionId(),
                                                     TestContext.Current.CancellationToken));
        Assert.Empty(await mAssignments.GetByDocumentRevisionIdsAsync(
                         [DirectoryPackagingFixtures.RevisionId()],
                         TestContext.Current.CancellationToken));
        Assert.Null(await mCatalogs.GetAsync(DirectoryPackagingFixtures.LibraryId,
                                              DirectoryPackagingFixtures.TaxonomyVersion,
                                              TestContext.Current.CancellationToken));
        Assert.Empty(await mPages.GetPagesAsync(DirectoryPackagingFixtures.LibraryId,
                                                 DirectoryPackagingFixtures.Version,
                                                 TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<FileNotFoundException>(() => mSources.OpenArtifactAsync(
                                                                 DirectoryPackagingFixtures.Hash(
                                                                     DirectoryPackagingFixtures.ExtractionBytes),
                                                                 TestContext.Current.CancellationToken));
        Assert.Equal(DirectoryPackagingFixtures.OriginalBytes,
                     await ReadArtifactAsync(DirectoryPackagingFixtures.Hash(
                                                 DirectoryPackagingFixtures.OriginalBytes)));
        Assert.NotNull(await mSources.GetRevisionAsync(SharedReceiverRevisionId,
                                                        TestContext.Current.CancellationToken));
        Assert.NotNull(await mLibraries.GetLibraryAsync(SharedReceiverLibraryId,
                                                         TestContext.Current.CancellationToken));
        Assert.Equal(1,
                     await mContext.DocumentArtifactBlobs.CountDocumentsAsync(
                         FilterDefinition<DocumentArtifactBlobRecord>.Empty,
                         cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task SeedDirectoryLibraryAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await mLibraries.UpsertLibraryAsync(DirectoryPackagingFixtures.Library(), ct);
        await mLibraries.UpsertVersionAsync(DirectoryPackagingFixtures.LibraryVersion(), ct);
        await mSources.UpsertDirectoryDefinitionAsync(DirectoryPackagingFixtures.DirectoryDefinition(), ct);
        await mSources.GetOrCreateDocumentAsync(DirectoryPackagingFixtures.Source(), ct);
        await PersistRevisionAsync(DirectoryPackagingFixtures.Revision(),
                                   DirectoryPackagingFixtures.OriginalBytes,
                                   DirectoryPackagingFixtures.ExtractionBytes);
        await mCatalogs.InsertRevisionAsync(DirectoryPackagingFixtures.Catalog(), ct);
        await mAssignments.PersistAsync(DirectoryPackagingFixtures.Assignment(), ct);
        await mPages.UpsertPageAsync(DirectoryPackagingFixtures.Page(), ct);
        await mChunks.InsertChunksAsync([DirectoryPackagingFixtures.Chunk()], ct);
    }

    private async Task SeedSharedReceiverReferenceAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await mLibraries.UpsertLibraryAsync(DirectoryPackagingFixtures.Library(SharedReceiverLibraryId), ct);
        await mLibraries.UpsertVersionAsync(DirectoryPackagingFixtures.LibraryVersion(SharedReceiverLibraryId), ct);
        SourceDocumentRecord source = DirectoryPackagingFixtures.Source(SharedReceiverLibraryId,
                                                                         documentId: SharedReceiverDocumentId);
        await mSources.GetOrCreateDocumentAsync(source, ct);
        DocumentRevisionRecord revision = DirectoryPackagingFixtures.Revision(SharedReceiverLibraryId,
                                                                               documentId:
                                                                               SharedReceiverDocumentId) with
                                              {
                                                  Id = SharedReceiverRevisionId,
                                                  ExtractionArtifactHash = null,
                                                  ExtractionByteLength = null,
                                                  ExtractionMediaType = null,
                                                  ExtractionProvenance = null
                                              };
        await PersistRevisionAsync(revision,
                                   DirectoryPackagingFixtures.OriginalBytes,
                                   extraction: null);
    }

    private async Task ExportAsync()
    {
        await Exporter().ExportAsync(new ExportRequest
                                         {
                                             LibraryId = DirectoryPackagingFixtures.LibraryId,
                                             Versions = VersionFilter.Current,
                                             OutputPath = mBundlePath
                                         },
                                     progress: null,
                                     TestContext.Current.CancellationToken);
    }

    private async Task DeleteLibraryAsync(string libraryId)
    {
        var deletion = new LibraryDeletionService(mFactory, new InMemoryBruteForceVectorSearch());
        await deletion.DeleteLibraryAsync(profile: null,
                                          libraryId,
                                          TestContext.Current.CancellationToken);
    }

    private LibraryExporter Exporter() => new(mLibraries,
                                               mProfiles,
                                               mIndexes,
                                               mExcluded,
                                               mDiffs,
                                               mPages,
                                               mChunks,
                                               mBm25,
                                               mSources,
                                               mCatalogs,
                                               mAssignments);

    private LibraryImporter Importer(IChunkRepository chunks) => new(mLibraries,
                                                                     mJobs,
                                                                     mEmbedding,
                                                                     mProfiles,
                                                                     mIndexes,
                                                                     mExcluded,
                                                                     mDiffs,
                                                                     mPages,
                                                                     chunks,
                                                                     mBm25,
                                                                     mSources,
                                                                     mCatalogs,
                                                                     mAssignments);

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

    private sealed class Stage7EmbeddingProvider : IEmbeddingProvider
    {
        public string ProviderId => DirectoryPackagingFixtures.EmbeddingProviderId;

        public string ModelName => DirectoryPackagingFixtures.EmbeddingModelName;

        public int Dimensions => DirectoryPackagingFixtures.EmbeddingDimensions;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts,
                                          EmbedRole role = EmbedRole.Document,
                                          CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(texts);
            ct.ThrowIfCancellationRequested();
            float[][] result = texts.Select(_ => new[] { 1.0f, 0.0f }).ToArray();
            return Task.FromResult(result);
        }
    }

    private static readonly string SharedReceiverRevisionId =
        DirectoryPackagingFixtures.RevisionId(SharedReceiverLibraryId,
                                              DirectoryPackagingFixtures.Version,
                                              SharedReceiverDocumentId);
    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string SharedReceiverLibraryId = "stage7-shared-receiver";
    private const string SharedReceiverDocumentId = "stage7-shared-document";
}
