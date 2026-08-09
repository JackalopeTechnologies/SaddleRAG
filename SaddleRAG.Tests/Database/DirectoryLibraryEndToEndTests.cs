// DirectoryLibraryEndToEndTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Ingestion.Subjects;
using SaddleRAG.Mcp.Tools;
using SaddleRAG.Tests.Ingestion;

namespace SaddleRAG.Tests.Database;

[Trait("Category", "Integration")]
public sealed class DirectoryLibraryEndToEndTests : IAsyncLifetime
{
    private string mDatabaseName = string.Empty;
    private string mProfileDatabaseName = string.Empty;
    private ServiceProvider mServices = null!;
    private SaddleRagDbContext mContext = null!;
    private RepositoryFactory mFactory = null!;
    private IDirectoryLibraryRegistrationService mRegistration = null!;
    private IDirectoryIngestionCoordinator mCoordinator = null!;
    private ScriptedDoclingClient mDocling = null!;
    private CountingEmbeddingProvider mEmbedding = null!;
    private InMemoryBruteForceVectorSearch mVector = null!;
    private MutableDirectoryTimeProvider mClock = null!;
    private DirectoryLibraryFixtureTree mFixture = null!;

    public async ValueTask InitializeAsync()
    {
        mDatabaseName = $"srd-e2e-{Guid.NewGuid():N}";
        mProfileDatabaseName = $"srd-profile-{Guid.NewGuid():N}";
        mFixture = DirectoryLibraryFixtureTree.Create();
        mClock = new MutableDirectoryTimeProvider(FirstQueuedAt);
        mDocling = new ScriptedDoclingClient();
        mEmbedding = new CountingEmbeddingProvider();
        mVector = new InMemoryBruteForceVectorSearch();
        IConfiguration configuration = new ConfigurationBuilder()
                                          .AddInMemoryCollection(new Dictionary<string, string?>
                                                                     {
                                                                         [$"{SaddleRagDbSettings.SectionName}:ConnectionString"] =
                                                                             TestConnectionString,
                                                                         [$"{SaddleRagDbSettings.SectionName}:DatabaseName"] =
                                                                             mDatabaseName,
                                                                         [$"{SaddleRagDbSettings.SectionName}:Profiles:{ProfileName}:ConnectionString"] =
                                                                             TestConnectionString,
                                                                         [$"{SaddleRagDbSettings.SectionName}:Profiles:{ProfileName}:DatabaseName"] =
                                                                             mProfileDatabaseName
                                                                     })
                                          .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSaddleRagDatabase(configuration);
        services.AddSaddleRagDirectoryIngestion();
        services.AddSingleton<TimeProvider>(mClock);
        services.AddSingleton<IDoclingClient>(mDocling);
        services.AddSingleton<ILlmClassifier, ScriptedFormClassifier>();
        services.AddSingleton<IClassifierTextGenerator, ScriptedSubjectTextGenerator>();
        services.AddSingleton<ISubjectIdGenerator, FixedSubjectIdGenerator>();
        services.AddSingleton<IEmbeddingProvider>(mEmbedding);
        services.AddSingleton<IVectorSearchProvider>(mVector);
        mServices = services.BuildServiceProvider(validateScopes: true);
        mContext = mServices.GetRequiredService<SaddleRagDbContext>();
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        mFactory = mServices.GetRequiredService<RepositoryFactory>();
        mRegistration = mServices.GetRequiredService<IDirectoryLibraryRegistrationService>();
        mCoordinator = mServices.GetRequiredService<IDirectoryIngestionCoordinator>();
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDatabaseName);
        await mContext.Database.Client.DropDatabaseAsync(mProfileDatabaseName);
        await mServices.DisposeAsync();
        mFixture.Dispose();
    }

    [Fact]
    public async Task ManualSnapshotsAreSearchableAndReuseCompatibleUnchangedDocuments()
    {
        DirectoryRegistrationResult registration = await RegisterAsync();

        DirectoryIngestionResult first = await ScanAsync(FirstVersion, "scan-first");

        Assert.Equal(DirectoryRegistrationStatuses.Registered, registration.Status);
        Assert.Equal(DirectoryIngestionStatuses.Completed, first.Status);
        Assert.Equal(FirstVersion, first.Version);
        LibraryRecord library = Assert.IsType<LibraryRecord>(await Libraries.GetLibraryAsync(
                                                                  LibraryId,
                                                                  TestContext.Current.CancellationToken));
        Assert.Equal(FirstVersion, library.CurrentVersion);
        Assert.Equal([FirstVersion], library.AllVersions);
        await AssertSearchCitationAsync(PdfMarker, "Manual.pdf", "Owned PDF heading", expectedPage: 2);
        await AssertSearchCitationAsync(DocxMarker, "Manual.docx", "Owned DOCX heading", expectedPage: 3);
        int firstDoclingConversions = mDocling.ConversionCount;
        int firstDocumentEmbeddings = mEmbedding.DocumentInputCount;
        Assert.Equal(2, firstDoclingConversions);
        Assert.True(firstDocumentEmbeddings >= 4);

        DirectoryIngestionResult duplicate = await ScanAsync(FirstVersion, "scan-duplicate");

        Assert.Equal(DirectoryScanVersionProvider.AlreadyScannedTodayStatus, duplicate.Status);
        Assert.Equal(firstDoclingConversions, mDocling.ConversionCount);
        Assert.Equal(firstDocumentEmbeddings, mEmbedding.DocumentInputCount);

        DocumentRevisionRecord firstPdfRevision = await RevisionForPathAsync(FirstVersion, "Manual.pdf");
        DocumentRevisionRecord firstDocxRevision = await RevisionForPathAsync(FirstVersion, "Manual.docx");
        IReadOnlyList<DocChunk> firstChunks = await Chunks.GetChunksAsync(LibraryId,
                                                                          FirstVersion,
                                                                          TestContext.Current.CancellationToken);
        mFixture.ChangeGuide();
        mFixture.AddNewDocument();
        mFixture.RemoveNotes();
        mClock.UtcNow = SecondQueuedAt;

        DirectoryIngestionResult second = await ScanAsync(SecondVersion, "scan-second");

        Assert.Equal(DirectoryIngestionStatuses.Completed, second.Status);
        library = Assert.IsType<LibraryRecord>(await Libraries.GetLibraryAsync(
                                                   LibraryId,
                                                   TestContext.Current.CancellationToken));
        Assert.Equal(SecondVersion, library.CurrentVersion);
        Assert.Equal([FirstVersion, SecondVersion], library.AllVersions);
        Assert.Equal(firstDoclingConversions + BinaryDocumentCount, mDocling.ConversionCount);
        Assert.Equal(2, mEmbedding.DocumentInputCount - firstDocumentEmbeddings);
        Assert.Contains(mEmbedding.DocumentInputs,
                        input => input.Contains("Changed guide marker", StringComparison.Ordinal));
        Assert.Contains(mEmbedding.DocumentInputs,
                        input => input.Contains("New document marker", StringComparison.Ordinal));

        DocumentRevisionRecord secondPdfRevision = await RevisionForPathAsync(SecondVersion, "Manual.pdf");
        DocumentRevisionRecord secondDocxRevision = await RevisionForPathAsync(SecondVersion, "Manual.docx");
        Assert.NotEqual(firstPdfRevision.Id, secondPdfRevision.Id);
        Assert.Equal(firstPdfRevision.OriginalArtifactHash, secondPdfRevision.OriginalArtifactHash);
        Assert.Equal(firstPdfRevision.ExtractionArtifactHash, secondPdfRevision.ExtractionArtifactHash);
        Assert.NotEqual(firstDocxRevision.Id, secondDocxRevision.Id);
        Assert.Equal(firstDocxRevision.OriginalArtifactHash, secondDocxRevision.OriginalArtifactHash);
        Assert.Equal(firstDocxRevision.ExtractionArtifactHash, secondDocxRevision.ExtractionArtifactHash);
        IReadOnlyList<DocChunk> secondChunks = await Chunks.GetChunksAsync(LibraryId,
                                                                           SecondVersion,
                                                                           TestContext.Current.CancellationToken);
        AssertReusedEmbeddings(firstChunks, secondChunks, "Manual.pdf");
        AssertReusedEmbeddings(firstChunks, secondChunks, "Manual.docx");

        IReadOnlyList<PageRecord> firstPages = await Pages.GetPagesAsync(LibraryId,
                                                                         FirstVersion,
                                                                         TestContext.Current.CancellationToken);
        IReadOnlyList<PageRecord> secondPages = await Pages.GetPagesAsync(LibraryId,
                                                                          SecondVersion,
                                                                          TestContext.Current.CancellationToken);
        Assert.Contains(firstPages,
                        page => page.DocumentSource?.RelativePath.Equals("Notes.txt",
                                                                         StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(secondPages,
                           page => page.DocumentSource?.RelativePath.Equals("Notes.txt",
                                                                            StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(secondPages,
                        page => page.DocumentSource?.RelativePath.Equals("New.txt",
                                                                         StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(secondPages,
                        page => page.RawContent.Contains("Changed guide marker", StringComparison.Ordinal));
        await AssertSearchCitationAsync(PdfMarker, "Manual.pdf", "Owned PDF heading", expectedPage: 2);
    }

    [Fact]
    public async Task ChangedEmbeddingManifestReusesExtractionButReembedsUnchangedDocuments()
    {
        await RegisterAsync();
        DirectoryIngestionResult first = await ScanAsync(FirstVersion, "scan-first");
        Assert.Equal(DirectoryIngestionStatuses.Completed, first.Status);
        int firstDoclingConversions = mDocling.ConversionCount;
        int firstDocumentEmbeddings = mEmbedding.DocumentInputCount;
        mEmbedding.ModelName = "scripted-embedding-v2";
        mClock.UtcNow = SecondQueuedAt;

        DirectoryIngestionResult second = await ScanAsync(SecondVersion, "scan-second");

        Assert.Equal(DirectoryIngestionStatuses.Completed, second.Status);
        Assert.Equal(firstDoclingConversions + BinaryDocumentCount, mDocling.ConversionCount);
        Assert.Equal(firstDocumentEmbeddings,
                     mEmbedding.DocumentInputCount - firstDocumentEmbeddings);
        LibraryVersionRecord version = Assert.IsType<LibraryVersionRecord>(await Libraries.GetVersionAsync(
                                                                               LibraryId,
                                                                               SecondVersion,
                                                                               TestContext.Current.CancellationToken));
        Assert.Equal("scripted-embedding-v2", version.EmbeddingModelName);
    }

    [Fact]
    public async Task FailedSupportedDocumentOnNextDatePublishesNothingAndPriorSnapshotStaysSearchable()
    {
        await RegisterAsync();
        DirectoryIngestionResult first = await ScanAsync(FirstVersion, "scan-first");
        Assert.Equal(DirectoryIngestionStatuses.Completed, first.Status);
        mClock.UtcNow = SecondQueuedAt;
        await File.AppendAllTextAsync(mFixture.PdfPath,
                                      "\nchanged bytes",
                                      TestContext.Current.CancellationToken);
        mDocling.FailPdf = true;

        DirectoryIngestionResult failed = await ScanAsync(SecondVersion, "scan-failed");

        Assert.Equal(DirectoryIngestionStatuses.Failed, failed.Status);
        Assert.Equal(DoclingReasonCodes.ConversionFailed, failed.ReasonCode);
        LibraryRecord library = Assert.IsType<LibraryRecord>(await Libraries.GetLibraryAsync(
                                                                  LibraryId,
                                                                  TestContext.Current.CancellationToken));
        Assert.Equal(FirstVersion, library.CurrentVersion);
        Assert.DoesNotContain(SecondVersion, library.AllVersions);
        LibraryVersionRecord? failedVersion = await Libraries.GetVersionAsync(
                                                  LibraryId,
                                                  SecondVersion,
                                                  TestContext.Current.CancellationToken);
        Assert.Equal(VersionPublicationState.Failed, failedVersion?.PublicationState);
        Assert.Empty(await Pages.GetPagesAsync(LibraryId,
                                               SecondVersion,
                                               TestContext.Current.CancellationToken));
        Assert.Empty(await Chunks.GetChunksAsync(LibraryId,
                                                 SecondVersion,
                                                 TestContext.Current.CancellationToken));
        IReadOnlyList<DocumentRevisionRecord> revisions = await Sources.GetRevisionsAsync(
                                                                 LibraryId,
                                                                 SecondVersion,
                                                                 TestContext.Current.CancellationToken);
        Assert.DoesNotContain(revisions, revision => revision.State == DocumentRevisionState.Candidate);
        await AssertSearchCitationAsync(PdfMarker, "Manual.pdf", "Owned PDF heading", expectedPage: 2);
    }

    [Fact]
    public async Task NonDefaultProfileOwnsItsSubjectCatalogAndAssignments()
    {
        SaddleRagDbContext profileContext = mServices.GetRequiredService<SaddleRagDbContextFactory>()
                                                   .GetForProfile(ProfileName);
        await profileContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

        DirectoryRegistrationResult registration = await RegisterAsync(ProfileName);
        DirectoryIngestionResult ingestion = await ScanAsync(FirstVersion, "scan-profile", ProfileName);

        Assert.Equal(DirectoryRegistrationStatuses.Registered, registration.Status);
        Assert.Equal(DirectoryIngestionStatuses.Completed, ingestion.Status);
        ISourceDocumentRepository profileSources = mFactory.GetSourceDocumentRepository(ProfileName);
        IReadOnlyList<DocumentRevisionRecord> revisions = await profileSources.GetRevisionsAsync(
                                                                LibraryId,
                                                                FirstVersion,
                                                                TestContext.Current.CancellationToken);
        Assert.NotEmpty(revisions);
        ISubjectCatalogRepository profileCatalogs = mFactory.GetSubjectCatalogRepository(ProfileName);
        SubjectCatalogRecord catalog = Assert.IsType<SubjectCatalogRecord>(await profileCatalogs.GetLatestAsync(
                                                                               LibraryId,
                                                                               TestContext.Current.CancellationToken));
        Assert.Equal("scan-profile", catalog.ScanRunId);
        ISubjectAssignmentRepository profileAssignments = mFactory.GetSubjectAssignmentRepository(ProfileName);
        IReadOnlyList<SubjectAssignmentRecord> assignments = await profileAssignments
            .GetByDocumentRevisionIdsAsync(revisions.Select(revision => revision.Id).ToList(),
                                           TestContext.Current.CancellationToken);
        Assert.Equal(revisions.Count, assignments.Count);
        Assert.All(assignments, assignment => Assert.Equal("scan-profile", assignment.ScanRunId));

        Assert.Null(await mFactory.GetSubjectCatalogRepository(profile: null)
                                  .GetLatestAsync(LibraryId, TestContext.Current.CancellationToken));
        Assert.Empty(await mFactory.GetSubjectAssignmentRepository(profile: null)
                                   .GetByDocumentRevisionIdsAsync(revisions.Select(revision => revision.Id).ToList(),
                                                                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CatalogCleanupDeletesOnlyScanOwnedCandidatesWithoutAnyReferences()
    {
        ISubjectCatalogRepository catalogs = mFactory.GetSubjectCatalogRepository(profile: null);
        await catalogs.InsertRevisionAsync(Catalog("scan-published",
                                                    "taxonomy-000001",
                                                    revision: 1,
                                                    SubjectCatalogPublicationState.Published),
                                           TestContext.Current.CancellationToken);
        await catalogs.InsertRevisionAsync(Catalog("scan-candidate", "taxonomy-000002", revision: 2),
                                           TestContext.Current.CancellationToken);
        await Libraries.UpsertVersionAsync(PublishedVersion(FirstVersion, "taxonomy-000001"),
                                           TestContext.Current.CancellationToken);

        long candidateDeleted = await catalogs.DeleteCandidateScanRunAsync(
                                    LibraryId,
                                    "scan-candidate",
                                    deletingVersion: null,
                                    ct: TestContext.Current.CancellationToken);
        long protectedDeleted = await catalogs.DeleteCandidateScanRunAsync(
                                    LibraryId,
                                    "scan-published",
                                    deletingVersion: null,
                                    ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, candidateDeleted);
        Assert.Equal(0, protectedDeleted);
        Assert.Null(await catalogs.GetAsync(LibraryId,
                                            "taxonomy-000002",
                                            TestContext.Current.CancellationToken));
        Assert.NotNull(await catalogs.GetAsync(LibraryId,
                                               "taxonomy-000001",
                                               TestContext.Current.CancellationToken));
        long deletedWithTarget = await catalogs.DeleteCandidateScanRunAsync(
                                     LibraryId,
                                     "scan-published",
                                     deletingVersion: FirstVersion,
                                     ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, deletedWithTarget);
        Assert.NotNull(await catalogs.GetAsync(LibraryId,
                                               "taxonomy-000001",
                                               TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedScanRollbackRemovesItsPromotedCatalogAndPreservesThePreviousPublishedCatalog()
    {
        ISubjectCatalogRepository catalogs = mFactory.GetSubjectCatalogRepository(profile: null);
        SubjectCatalogRecord previous = Catalog("scan-previous",
                                                "taxonomy-000001",
                                                revision: 1,
                                                SubjectCatalogPublicationState.Published);
        await catalogs.InsertRevisionAsync(previous, TestContext.Current.CancellationToken);
        await catalogs.InsertRevisionAsync(Catalog("scan-failed", "taxonomy-000002", revision: 2),
                                           TestContext.Current.CancellationToken);
        await Libraries.UpsertVersionAsync(PublishedVersion(FirstVersion, previous.TaxonomyVersion),
                                           TestContext.Current.CancellationToken);
        await Libraries.UpsertVersionAsync(PublishedVersion(SecondVersion, "taxonomy-000002"),
                                           TestContext.Current.CancellationToken);
        Assert.True(await catalogs.TryPublishCandidateAsync(LibraryId,
                                                             "taxonomy-000002",
                                                             "scan-failed",
                                                             TestContext.Current.CancellationToken));
        SubjectCatalogRecord promoted = Assert.IsType<SubjectCatalogRecord>(
            await catalogs.GetLatestAsync(LibraryId, TestContext.Current.CancellationToken));

        Assert.False(await catalogs.TryRollbackCandidatePublicationAsync(
                         LibraryId,
                         previous.TaxonomyVersion,
                         "scan-failed",
                         TestContext.Current.CancellationToken));
        bool rolledBack = await catalogs.TryRollbackCandidatePublicationAsync(
                              LibraryId,
                              "taxonomy-000002",
                              "scan-failed",
                              TestContext.Current.CancellationToken);
        SubjectCatalogRecord restored = Assert.IsType<SubjectCatalogRecord>(
            await catalogs.GetLatestAsync(LibraryId, TestContext.Current.CancellationToken));
        SubjectCatalogRecord rolledBackCandidate = Assert.IsType<SubjectCatalogRecord>(
            await catalogs.GetAsync(LibraryId,
                                    "taxonomy-000002",
                                    TestContext.Current.CancellationToken));
        long deleted = await catalogs.DeleteCandidateScanRunAsync(LibraryId,
                                                                   "scan-failed",
                                                                   deletingVersion: SecondVersion,
                                                                   ct: TestContext.Current.CancellationToken);

        Assert.Equal("taxonomy-000002", promoted.TaxonomyVersion);
        Assert.True(rolledBack);
        Assert.Equal(SubjectCatalogPublicationState.Candidate,
                     rolledBackCandidate.PublicationState);
        Assert.Equal(1, deleted);
        Assert.Null(await catalogs.GetAsync(LibraryId,
                                            "taxonomy-000002",
                                            TestContext.Current.CancellationToken));
        Assert.Equal(previous.Id, restored.Id);
        Assert.Equal(previous.ScanRunId, restored.ScanRunId);
        Assert.Equal(previous.TaxonomyVersion, restored.TaxonomyVersion);
        Assert.Equal(previous.Revision, restored.Revision);
        Assert.Equal(SubjectCatalogPublicationState.Published, restored.PublicationState);
    }

    [Fact]
    public async Task CandidateCatalogIsInvisibleUntilItsExactOwnerPublishesIt()
    {
        ISubjectCatalogRepository catalogs = mFactory.GetSubjectCatalogRepository(profile: null);
        await catalogs.InsertRevisionAsync(Catalog("scan-published",
                                                    "taxonomy-000001",
                                                    revision: 1,
                                                    SubjectCatalogPublicationState.Published),
                                           TestContext.Current.CancellationToken);
        await catalogs.InsertRevisionAsync(Catalog("scan-candidate", "taxonomy-000002", revision: 2),
                                           TestContext.Current.CancellationToken);

        SubjectCatalogRecord before = Assert.IsType<SubjectCatalogRecord>(
            await catalogs.GetLatestAsync(LibraryId, TestContext.Current.CancellationToken));
        bool wrongOwner = await catalogs.TryPublishCandidateAsync(LibraryId,
                                                                   "taxonomy-000002",
                                                                   "other-scan",
                                                                   TestContext.Current.CancellationToken);
        bool published = await catalogs.TryPublishCandidateAsync(LibraryId,
                                                                  "taxonomy-000002",
                                                                  "scan-candidate",
                                                                  TestContext.Current.CancellationToken);
        SubjectCatalogRecord after = Assert.IsType<SubjectCatalogRecord>(
            await catalogs.GetLatestAsync(LibraryId, TestContext.Current.CancellationToken));

        Assert.Equal("taxonomy-000001", before.TaxonomyVersion);
        Assert.False(wrongOwner);
        Assert.True(published);
        Assert.Equal("taxonomy-000002", after.TaxonomyVersion);
        Assert.Equal(SubjectCatalogPublicationState.Published, after.PublicationState);
    }

    [Fact]
    public async Task LegacyCatalogWithoutPublicationStateRemainsVisibleAndPublicationCompatible()
    {
        ISubjectCatalogRepository catalogs = mFactory.GetSubjectCatalogRepository(profile: null);
        SubjectCatalogRecord legacy = Catalog("scan-legacy",
                                               "taxonomy-000001",
                                               revision: 1,
                                               SubjectCatalogPublicationState.Published);
        BsonDocument rawLegacy = legacy.ToBsonDocument();
        rawLegacy.Remove(nameof(SubjectCatalogRecord.PublicationState));
        IMongoCollection<BsonDocument> rawCatalogs =
            mContext.Database.GetCollection<BsonDocument>("subjectCatalogs");
        await rawCatalogs.InsertOneAsync(rawLegacy,
                                         cancellationToken: TestContext.Current.CancellationToken);
        await catalogs.InsertRevisionAsync(Catalog("scan-candidate", "taxonomy-000002", revision: 2),
                                           TestContext.Current.CancellationToken);

        SubjectCatalogRecord latest = Assert.IsType<SubjectCatalogRecord>(
            await catalogs.GetLatestAsync(LibraryId, TestContext.Current.CancellationToken));
        bool publicationCompatible = await catalogs.TryPublishCandidateAsync(
                                         LibraryId,
                                         legacy.TaxonomyVersion,
                                         "different-scan",
                                         TestContext.Current.CancellationToken);
        FilterDefinition<BsonDocument> legacyFilter = Builders<BsonDocument>.Filter.Eq("_id", legacy.Id);
        BsonDocument persisted = await rawCatalogs.Find(legacyFilter)
                                                   .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(legacy.TaxonomyVersion, latest.TaxonomyVersion);
        Assert.Equal(SubjectCatalogPublicationState.Published, latest.PublicationState);
        Assert.True(publicationCompatible);
        Assert.False(persisted.Contains(nameof(SubjectCatalogRecord.PublicationState)));
    }

    [Fact]
    public async Task CandidatePromotionAndCleanupCannotPublishAMissingCatalog()
    {
        ISubjectCatalogRepository catalogs = mFactory.GetSubjectCatalogRepository(profile: null);
        await catalogs.InsertRevisionAsync(Catalog("scan-race", "taxonomy-000001", revision: 1),
                                           TestContext.Current.CancellationToken);

        Task<bool> promotion = catalogs.TryPublishCandidateAsync(LibraryId,
                                                                  "taxonomy-000001",
                                                                  "scan-race",
                                                                  TestContext.Current.CancellationToken);
        Task<long> cleanup = catalogs.DeleteCandidateScanRunAsync(LibraryId,
                                                                   "scan-race",
                                                                   deletingVersion: null,
                                                                   ct: TestContext.Current.CancellationToken);
        bool promotionSucceeded = await promotion;
        long deleted = await cleanup;
        SubjectCatalogRecord? stored = await catalogs.GetAsync(LibraryId,
                                                                "taxonomy-000001",
                                                                TestContext.Current.CancellationToken);

        Assert.NotEqual(promotionSucceeded, deleted == 1);
        if (promotionSucceeded)
        {
            Assert.NotNull(stored);
            Assert.Equal(SubjectCatalogPublicationState.Published, stored.PublicationState);
        }
        else
        {
            Assert.Null(stored);
        }
    }

    [Fact]
    public async Task CatalogCleanupDefersForBuildingVersionsAndKeepsAssignmentReferences()
    {
        ISubjectCatalogRepository catalogs = mFactory.GetSubjectCatalogRepository(profile: null);
        ISubjectAssignmentRepository assignments = mFactory.GetSubjectAssignmentRepository(profile: null);
        await catalogs.InsertRevisionAsync(Catalog("scan-protected", "taxonomy-000003", revision: 3),
                                           TestContext.Current.CancellationToken);
        await catalogs.InsertRevisionAsync(Catalog("scan-protected", "taxonomy-000004", revision: 4),
                                           TestContext.Current.CancellationToken);
        await Libraries.UpsertVersionAsync(PublishedVersion("building-version", "taxonomy-000003") with
                                               {
                                                   PublicationState = VersionPublicationState.Building
                                               },
                                           TestContext.Current.CancellationToken);
        await assignments.PersistAsync(Assignment("assignment-version", "taxonomy-000004"),
                                       TestContext.Current.CancellationToken);

        long deferred = await catalogs.DeleteCandidateScanRunAsync(
                            LibraryId,
                            "scan-protected",
                            deletingVersion: null,
                            ct: TestContext.Current.CancellationToken);
        await Libraries.DeleteVersionAsync(LibraryId,
                                           "building-version",
                                           TestContext.Current.CancellationToken);
        long deletedAfterBuild = await catalogs.DeleteCandidateScanRunAsync(
                                     LibraryId,
                                     "scan-protected",
                                     deletingVersion: null,
                                     ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, deferred);
        Assert.Equal(1, deletedAfterBuild);
        Assert.Null(await catalogs.GetAsync(LibraryId,
                                            "taxonomy-000003",
                                            TestContext.Current.CancellationToken));
        Assert.NotNull(await catalogs.GetAsync(LibraryId,
                                               "taxonomy-000004",
                                               TestContext.Current.CancellationToken));
    }

    private ILibraryRepository Libraries => mFactory.GetLibraryRepository(profile: null);

    private IPageRepository Pages => mFactory.GetPageRepository(profile: null);

    private IChunkRepository Chunks => mFactory.GetChunkRepository(profile: null);

    private ISourceDocumentRepository Sources => mFactory.GetSourceDocumentRepository(profile: null);

    private Task<DirectoryRegistrationResult> RegisterAsync(string? profile = null) => mRegistration.RegisterAsync(
        new DirectoryRegistrationRequest(LibraryId,
                                         mFixture.RootPath,
                                         Recursive: true,
                                         ExclusionPatterns: []),
        profile,
        TestContext.Current.CancellationToken);

    private async Task<DirectoryIngestionResult> ScanAsync(string version,
                                                           string scanRunId,
                                                           string? profile = null)
    {
        ISourceDocumentRepository sources = mFactory.GetSourceDocumentRepository(profile);
        DirectoryLibraryDefinition? definition = await sources.GetDirectoryDefinitionAsync(
                                                      LibraryId,
                                                      TestContext.Current.CancellationToken);
        if (definition == null)
            throw new InvalidOperationException("The directory fixture must be registered before scanning.");
        var request = new DirectoryIngestionRequest
                          {
                              LibraryId = LibraryId,
                              Version = version,
                              QueuedAt = mClock.GetLocalNow(),
                              ScanRunId = scanRunId,
                              Definition = definition,
                              Profile = profile
                          };
        DirectoryIngestionResult result = await mCoordinator.RunAsync(request,
                                                                       onProgress: null,
                                                                       TestContext.Current.CancellationToken);
        return result;
    }

    private async Task<DocumentRevisionRecord> RevisionForPathAsync(string version, string relativePath)
    {
        IReadOnlyList<PageRecord> pages = await Pages.GetPagesAsync(LibraryId,
                                                                    version,
                                                                    TestContext.Current.CancellationToken);
        PageRecord page = Assert.Single(pages.Where(item => item.DocumentSource?.RelativePath.Equals(
                                                               relativePath,
                                                               StringComparison.OrdinalIgnoreCase) == true)
                                             .GroupBy(item => item.DocumentSource!.RevisionId,
                                                      StringComparer.Ordinal)
                                             .Select(group => group.First()));
        return Assert.IsType<DocumentRevisionRecord>(await Sources.GetRevisionAsync(
                                                         page.DocumentSource!.RevisionId,
                                                         TestContext.Current.CancellationToken));
    }

    private static SubjectCatalogRecord Catalog(
        string scanRunId,
        string taxonomyVersion,
        int revision,
        SubjectCatalogPublicationState publicationState = SubjectCatalogPublicationState.Candidate) => new()
        {
            Id = SubjectCatalogRepository.MakeId(LibraryId, taxonomyVersion),
            LibraryId = LibraryId,
            Revision = revision,
            TaxonomyVersion = taxonomyVersion,
            ScanRunId = scanRunId,
            PublicationState = publicationState,
            Concepts = [],
            Provenance = new SubjectClassifierProvenance
                             {
                                 Backend = "scripted",
                                 ModelId = "scripted-subject-v1",
                                 PromptVersion = SubjectCatalogPrompt.PromptVersion,
                                 GeneratedAtUtc = FirstQueuedAt.UtcDateTime
                             },
            CreatedAtUtc = FirstQueuedAt.UtcDateTime
        };

    private static LibraryVersionRecord PublishedVersion(string version, string taxonomyVersion) => new()
        {
            Id = $"{LibraryId}/{version}",
            LibraryId = LibraryId,
            Version = version,
            ScrapedAt = FirstQueuedAt.UtcDateTime,
            PageCount = 1,
            ChunkCount = 1,
            EmbeddingProviderId = "scripted",
            EmbeddingModelName = "scripted-embedding-v1",
            EmbeddingDimensions = 2,
            SubjectTaxonomyVersion = taxonomyVersion,
            PublicationState = VersionPublicationState.Published
        };

    private static SubjectAssignmentRecord Assignment(string version, string taxonomyVersion) => new()
        {
            Id = SubjectAssignmentRepository.MakeId(LibraryId, version, $"revision-{version}"),
            LibraryId = LibraryId,
            Version = version,
            ScanRunId = $"scan-{version}",
            DocumentId = $"document-{version}",
            DocumentRevisionId = $"revision-{version}",
            TaxonomyVersion = taxonomyVersion,
            Primary = new SubjectSelection
                          {
                              SubjectId = "subject",
                              Confidence = 1.0f
                          },
            NeedsReview = false,
            Provenance = Catalog("assignment", taxonomyVersion, revision: 1).Provenance
        };

    private static void AssertReusedEmbeddings(IReadOnlyList<DocChunk> prior,
                                               IReadOnlyList<DocChunk> current,
                                               string relativePath)
    {
        IReadOnlyList<DocChunk> priorDocument = prior.Where(chunk =>
                                                         chunk.DocumentSource?.RelativePath.Equals(
                                                             relativePath,
                                                             StringComparison.OrdinalIgnoreCase) == true)
                                                    .OrderBy(chunk => chunk.SectionPath, StringComparer.Ordinal)
                                                    .ToList();
        IReadOnlyList<DocChunk> currentDocument = current.Where(chunk =>
                                                             chunk.DocumentSource?.RelativePath.Equals(
                                                                 relativePath,
                                                                 StringComparison.OrdinalIgnoreCase) == true)
                                                        .OrderBy(chunk => chunk.SectionPath, StringComparer.Ordinal)
                                                        .ToList();
        Assert.Equal(priorDocument.Count, currentDocument.Count);
        for(var i = 0; i < priorDocument.Count; i++)
        {
            Assert.NotEqual(priorDocument[i].Id, currentDocument[i].Id);
            Assert.Equal(priorDocument[i].Embedding, currentDocument[i].Embedding);
        }
    }

    private async Task AssertSearchCitationAsync(string marker,
                                                 string relativePath,
                                                 string heading,
                                                 int expectedPage)
    {
        var reranker = Substitute.For<IReRanker>();
        var metrics = Substitute.For<IQueryMetrics>();
        string json = await SearchTools.SearchDocs(mVector,
                                                   mEmbedding,
                                                   reranker,
                                                   mFactory,
                                                   Options.Create(new RankingSettings
                                                                      {
                                                                          ReRankerStrategy = ReRankerStrategy.Off
                                                                      }),
                                                   metrics,
                                                   NullLogger<SearchTools.SearchToolsLog>.Instance,
                                                   marker,
                                                   library: LibraryId,
                                                   category: null,
                                                   subject: null,
                                                   version: null,
                                                   maxResults: 20,
                                                   profile: null,
                                                   TestContext.Current.CancellationToken);
        JsonArray results = JsonNode.Parse(json)!["Results"]!.AsArray();
        JsonObject match = Assert.Single(results.Select(node => node!.AsObject()),
                                         item => item["Content"]!.GetValue<string>()
                                                     .Contains(marker, StringComparison.Ordinal));
        JsonObject source = match["DocumentSource"]!.AsObject();
        Assert.Equal(relativePath, source["RelativePath"]!.GetValue<string>(), ignoreCase: true);
        Assert.Equal(expectedPage, source["PageStart"]!.GetValue<int>());
        Assert.Equal(expectedPage, source["PageEnd"]!.GetValue<int>());
        Assert.Equal(heading, source["Heading"]!.GetValue<string>());
        Assert.StartsWith($"saddlerag://library/{LibraryId}/documents/",
                          source["SourceUri"]!.GetValue<string>(),
                          StringComparison.Ordinal);
        Assert.NotNull(match["Subjects"]);
        Assert.DoesNotContain(mFixture.RootPath, json, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ScriptedDoclingClient : IDoclingClient
    {
        public bool FailPdf { get; set; }

        public int ConversionCount { get; private set; }

        public Task<DoclingServiceObservation> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DoclingServiceObservation.Success());

        public Task<DoclingServiceObservation> CheckReadinessAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DoclingServiceObservation.Success());

        public Task<DoclingConversionResult> ConvertAsync(DoclingFile file,
                                                          CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConversionCount++;
            bool isPdf = file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
            if (isPdf && FailPdf)
            {
                return Task.FromResult(DoclingConversionResult.Failure(
                                           DoclingReasonCodes.ConversionFailed,
                                           "The scripted PDF conversion failed."));
            }

            string marker = isPdf ? PdfMarker : DocxMarker;
            string heading = isPdf ? "Owned PDF heading" : "Owned DOCX heading";
            int page = isPdf ? 2 : 3;
            var blocks = new List<DoclingBlock>
                             {
                                 new(0,
                                     DoclingBlockKind.Heading,
                                     "section_header",
                                     heading,
                                     HeadingLevel: 1,
                                     PageNumber: page,
                                     BoundingBox: null,
                                     TableCells: []),
                                 new(1,
                                     DoclingBlockKind.Text,
                                     "text",
                                     marker,
                                     HeadingLevel: null,
                                     PageNumber: page,
                                     BoundingBox: null,
                                     TableCells: [])
                             };
            string raw = $"{{\"file\":\"{file.FileName}\",\"marker\":\"{marker}\"}}";
            var mapped = new DoclingMappedDocument(file.FileName,
                                                   $"# {heading}\n{marker}",
                                                   $"{heading}\n{marker}",
                                                   raw,
                                                   raw,
                                                   blocks);
            return Task.FromResult(DoclingConversionResult.Success(mapped));
        }
    }

    private sealed class ScriptedFormClassifier : ILlmClassifier
    {
        public string BackendName => "scripted";

        public string ModelId => "scripted-form-v1";

        public Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                            string libraryHint,
                                                                            CancellationToken ct = default) =>
            Task.FromResult((DocCategory.HowTo, 0.99f));

        public string GetCurrentVersion() => "scripted-form-v1/prompt-v1";
    }

    private sealed class ScriptedSubjectTextGenerator : IClassifierTextGenerator
    {
        public string BackendName => "scripted";

        public string ModelId => "scripted-subject-v1";

        public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            bool catalogPrompt = prompt.Contains(SubjectCatalogPrompt.PromptVersion, StringComparison.Ordinal);
            string response = catalogPrompt
                                  ? CatalogResponse(prompt)
                                  : AssignmentResponse;
            return Task.FromResult(response);
        }

        private static string CatalogResponse(string prompt) =>
            prompt.Contains(SubjectId, StringComparison.Ordinal)
                ? ExistingCatalogResponse
                : NewCatalogResponse;

        private const string NewCatalogResponse =
            "{\"concepts\":[{\"subjectId\":null,\"label\":\"Owned manuals\",\"aliases\":[\"manual\"],\"description\":\"Owned manual fixture documents.\"}]}";
        private const string ExistingCatalogResponse =
            "{\"concepts\":[{\"subjectId\":\"subject-owned-manuals\",\"label\":\"Owned manuals\",\"aliases\":[\"manual\"],\"description\":\"Owned manual fixture documents.\"}]}";
        private const string AssignmentResponse =
            "{\"primary\":{\"subjectId\":\"subject-owned-manuals\",\"confidence\":0.99,\"evidence\":[\"owned manual fixture\"]},\"secondary\":[]}";
        private const string SubjectId = "subject-owned-manuals";
    }

    private sealed class FixedSubjectIdGenerator : ISubjectIdGenerator
    {
        public string CreateId() => "subject-owned-manuals";
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public List<string> DocumentInputs { get; } = [];

        public int DocumentInputCount => DocumentInputs.Count;

        public string ProviderId => "scripted";

        public string ModelName { get; set; } = "scripted-embedding-v1";

        public int Dimensions => 2;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts,
                                          EmbedRole role = EmbedRole.Document,
                                          CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (role == EmbedRole.Document)
                DocumentInputs.AddRange(texts);
            return Task.FromResult(texts.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }

    private sealed class MutableDirectoryTimeProvider : TimeProvider
    {
        public MutableDirectoryTimeProvider(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private static readonly DateTimeOffset FirstQueuedAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondQueuedAt = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string ProfileName = "manual-profile";
    private const string LibraryId = "manual-library";
    private const string FirstVersion = "2026-08-04";
    private const string SecondVersion = "2026-08-05";
    private const string PdfMarker = "STAGE6_OWNED_PDF_MARKER";
    private const string DocxMarker = "STAGE6_OWNED_DOCX_MARKER";
    private const int BinaryDocumentCount = 2;
}
