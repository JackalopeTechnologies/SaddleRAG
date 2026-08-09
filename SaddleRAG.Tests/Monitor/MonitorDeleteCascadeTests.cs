// MonitorDeleteCascadeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Ingestion.Services;
using SaddleRAG.Mcp.Api;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorDeleteCascadeTests
{
    [Fact]
    public async Task DeleteVersionCascadeRemovesEveryVersionStoreAndExactVectorIndexBeforeMetadata()
    {
        var events = new List<string>();
        (var service, var repos, var vector) = BuildService();
        ConfigureDeleteEvents(repos, events);
        vector.RemoveIndexAsync(null, "lib", "v1", Arg.Any<CancellationToken>())
              .Returns(_ => { events.Add("vector"); return Task.CompletedTask; });
        repos.Library.DeleteVersionAsync("lib", "v1", Arg.Any<CancellationToken>())
             .Returns(_ =>
                  {
                      events.Add("metadata");
                      return new DeleteVersionResult(1, LibraryRowDeleted: false, "v0");
                  });

        var result = await service.DeleteVersionAsync(null,
                                                      "lib",
                                                      "v1",
                                                      TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Versions);
        Assert.Equal(1, result.DocumentRevisions);
        Assert.Equal(1, result.SubjectAssignments);
        Assert.Equal(1, result.SubjectCatalogs);
        Assert.Equal(1, result.Diffs);
        Assert.Equal(1, result.Jobs);
        Assert.Equal("metadata", events[^1]);
        Assert.Equal("vector", events[^2]);
        Assert.Equal(14, events.Count);
        await repos.SubjectCatalogs.Received(1)
                   .DeleteCandidateScanRunAsync("lib", "scan-v1", "v1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChildDeletionFailureAttemptsRemainingCleanupAndPreservesOriginalFailureAndMetadata()
    {
        (var service, var repos, var vector) = BuildService();
        var original = new InvalidOperationException("chunk-delete-failed");
        repos.SourceDocuments.GetRevisionsAsync("lib", "v1", Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<DocumentRevisionRecord>>([Revision("v1")]));
        repos.Chunks.DeleteChunksAsync("lib", "v1", Arg.Any<CancellationToken>())
             .Returns(Task.FromException<long>(original));
        repos.Pages.DeleteAsync("lib", "v1", Arg.Any<CancellationToken>())
             .Returns(Task.FromException<long>(new IOException("page-delete-failed")));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteVersionAsync(
            null, "lib", "v1", TestContext.Current.CancellationToken));

        Assert.Same(original, thrown);
        await repos.Pages.Received(1).DeleteAsync("lib", "v1", Arg.Any<CancellationToken>());
        await repos.SourceDocuments.Received(1)
                   .DeleteVersionAsync("lib", "v1", Arg.Any<CancellationToken>());
        await repos.SubjectAssignments.Received(1)
                   .DeleteVersionAsync("lib", "v1", Arg.Any<CancellationToken>());
        await repos.SubjectCatalogs.Received(1)
                   .DeleteCandidateScanRunAsync("lib", "scan-v1", "v1", Arg.Any<CancellationToken>());
        await vector.Received(1).RemoveIndexAsync(null, "lib", "v1", Arg.Any<CancellationToken>());
        await repos.Library.DidNotReceive()
                   .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BusyDirectoryLifecycleLeaseRejectsVersionDeletionBeforeAnyMutation()
    {
        (var service, var repos, var vector) = BuildService();
        repos.SourceDocuments.GetDirectoryDefinitionAsync("lib", Arg.Any<CancellationToken>())
             .Returns(DirectoryDefinition());
        repos.SourceDocuments.TryAcquireDirectoryPublicationLeaseAsync(Arg.Any<string>(),
                                                                        Arg.Any<long>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<string>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<CancellationToken>())
             .Returns((IDirectoryPublicationLease?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteVersionAsync(
            null, "lib", "v1", TestContext.Current.CancellationToken));

        await AssertNoVersionMutationsAsync(repos, vector);
        await repos.Library.DidNotReceive()
                   .GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LostDirectoryLifecycleLeaseRejectsVersionDeletionBeforeAnyMutation()
    {
        (var service, var repos, var vector) = BuildService();
        var lease = new ScriptedDirectoryPublicationLease([true, false]);
        repos.SourceDocuments.GetDirectoryDefinitionAsync("lib", Arg.Any<CancellationToken>())
             .Returns(DirectoryDefinition());
        repos.SourceDocuments.TryAcquireDirectoryPublicationLeaseAsync(Arg.Any<string>(),
                                                                        Arg.Any<long>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<string>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<CancellationToken>())
             .Returns(lease);
        repos.Library.GetVersionAsync("lib", "v1", Arg.Any<CancellationToken>()).Returns(Version("v1"));
        repos.Library.GetVersionsAsync("lib", Arg.Any<CancellationToken>()).Returns([Version("v1")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteVersionAsync(
            null, "lib", "v1", TestContext.Current.CancellationToken));

        Assert.Equal(2, lease.RenewalCount);
        await AssertNoVersionMutationsAsync(repos, vector);
    }

    [Fact]
    public async Task OwnershipLossDuringVersionCleanupStopsEveryLaterMutation()
    {
        (var service, var repos, var vector) = BuildService();
        var lease = new ScriptedDirectoryPublicationLease([true, true]);
        repos.SourceDocuments.GetDirectoryDefinitionAsync("lib", Arg.Any<CancellationToken>())
             .Returns(DirectoryDefinition());
        repos.SourceDocuments.TryAcquireDirectoryPublicationLeaseAsync(Arg.Any<string>(),
                                                                        Arg.Any<long>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<string>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<CancellationToken>())
             .Returns(lease);
        repos.Library.GetVersionAsync("lib", "v1", Arg.Any<CancellationToken>()).Returns(Version("v1"));
        repos.Library.GetVersionsAsync("lib", Arg.Any<CancellationToken>()).Returns([Version("v1")]);
        repos.Chunks.DeleteChunksAsync("lib", "v1", Arg.Any<CancellationToken>())
             .Returns(_ =>
                  {
                      lease.LoseOwnership();
                      return 1L;
                  });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteVersionAsync(
            null, "lib", "v1", TestContext.Current.CancellationToken));

        await repos.Pages.DidNotReceive()
                   .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SourceDocuments.DidNotReceive()
                   .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SubjectAssignments.DidNotReceive()
                   .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await vector.DidNotReceive()
                    .RemoveIndexAsync(Arg.Any<string?>(),
                                      Arg.Any<string>(),
                                      Arg.Any<string>(),
                                      Arg.Any<CancellationToken>());
        await repos.Library.DidNotReceive()
                   .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SourceDocuments.DidNotReceive()
                   .TryDeleteLeasedDirectoryDefinitionAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                             Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteLibraryCascadeInvalidatesAllLibraryVectorIndexesBeforeMetadata()
    {
        var events = new List<string>();
        (var service, var repos, var vector) = BuildService();
        repos.Library.GetLibraryAsync("lib", Arg.Any<CancellationToken>())
             .Returns(new LibraryRecord
                          {
                              Id = "lib", Name = "lib", Hint = "h", CurrentVersion = "v2",
                              AllVersions = ["v1", "v2"]
                          });
        repos.Library.GetVersionsAsync("lib", Arg.Any<CancellationToken>())
             .Returns(new[] { Version("v1"), Version("v2") });
        ConfigureDeleteEvents(repos, events);
        vector.RemoveLibraryIndexesAsync(null, "lib", Arg.Any<CancellationToken>())
              .Returns(_ => { events.Add("vector-library"); return Task.CompletedTask; });
        repos.Library.DeleteAsync("lib", Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("metadata-library"); return 2L; });

        var result = await service.DeleteLibraryAsync(null, "lib", TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Versions);
        Assert.Equal(2, result.DocumentRevisions);
        Assert.Equal(2, result.SubjectAssignments);
        Assert.Equal(2, result.SubjectCatalogs);
        Assert.Equal(2, result.Diffs);
        Assert.Equal(2, result.Jobs);
        Assert.Equal(1, result.ProjectProfiles);
        Assert.Equal("vector-library", events[^2]);
        Assert.Equal("metadata-library", events[^1]);
        await repos.SourceDocuments.Received(1)
                   .DeleteLibraryAsync("lib", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BusyDirectoryLifecycleLeaseRejectsLibraryDeletionBeforeAnyMutation()
    {
        (var service, var repos, var vector) = BuildService();
        repos.SourceDocuments.GetDirectoryDefinitionAsync("lib", Arg.Any<CancellationToken>())
             .Returns(DirectoryDefinition());
        repos.SourceDocuments.TryAcquireDirectoryPublicationLeaseAsync(Arg.Any<string>(),
                                                                        Arg.Any<long>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<string>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<CancellationToken>())
             .Returns((IDirectoryPublicationLease?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteLibraryAsync(
            null, "lib", TestContext.Current.CancellationToken));

        await AssertNoLibraryMutationsAsync(repos, vector);
        await repos.Library.DidNotReceive()
                   .GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LostDirectoryLifecycleLeasePreservesLibraryMetadataAndDefinition()
    {
        (var service, var repos, var vector) = BuildService();
        var lease = new ScriptedDirectoryPublicationLease([true, true, false]);
        repos.SourceDocuments.GetDirectoryDefinitionAsync("lib", Arg.Any<CancellationToken>())
             .Returns(DirectoryDefinition());
        repos.SourceDocuments.TryAcquireDirectoryPublicationLeaseAsync(Arg.Any<string>(),
                                                                        Arg.Any<long>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<string>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<CancellationToken>())
             .Returns(lease);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteLibraryAsync(
            null, "lib", TestContext.Current.CancellationToken));

        Assert.Equal(3, lease.RenewalCount);
        await repos.Library.DidNotReceive()
                   .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SourceDocuments.DidNotReceive()
                   .TryDeleteLeasedDirectoryDefinitionAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                             Arg.Any<CancellationToken>());
        await vector.Received(1)
                    .RemoveLibraryIndexesAsync(null, "lib", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DirectoryLibraryDeletionRemovesExactDefinitionAfterMetadata()
    {
        var events = new List<string>();
        (var service, var repos, var vector) = BuildService();
        var lease = new ScriptedDirectoryPublicationLease([true, true, true, true]);
        repos.SourceDocuments.GetDirectoryDefinitionAsync("lib", Arg.Any<CancellationToken>())
             .Returns(DirectoryDefinition());
        repos.SourceDocuments.TryAcquireDirectoryPublicationLeaseAsync(Arg.Any<string>(),
                                                                        Arg.Any<long>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<string>(),
                                                                        Arg.Any<string?>(),
                                                                        Arg.Any<CancellationToken>())
             .Returns(lease);
        vector.RemoveLibraryIndexesAsync(null, "lib", Arg.Any<CancellationToken>())
              .Returns(_ => { events.Add("vector-library"); return Task.CompletedTask; });
        repos.Library.DeleteAsync("lib", Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("metadata-library"); return 0L; });
        repos.SourceDocuments.TryDeleteLeasedDirectoryDefinitionAsync(lease,
                                                                       Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("definition"); return true; });

        await service.DeleteLibraryAsync(null, "lib", TestContext.Current.CancellationToken);

        Assert.Equal(["vector-library", "metadata-library", "definition"], events);
        await repos.SourceDocuments.Received(1)
                   .TryDeleteLeasedDirectoryDefinitionAsync(lease, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MonitorDeleteVersionDelegatesOnceToCascade()
    {
        var deletion = Substitute.For<ILibraryDeletionService>();
        deletion.DeleteVersionAsync(null, "lib", "v1", Arg.Any<CancellationToken>())
                .Returns(EmptyResult with { Versions = 1 });

        await MonitorLibraryActionsEndpoints.DeleteVersionAsync("lib",
                                                                 "v1",
                                                                 deletion,
                                                                 TestContext.Current.CancellationToken);

        await deletion.Received(1)
                      .DeleteVersionAsync(null, "lib", "v1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task McpDeleteVersionDelegatesOnceToSameCascade()
    {
        var deletion = Substitute.For<ILibraryDeletionService>();
        deletion.DeleteVersionPreservingJobAsync(Arg.Is<string?>(value => value == null),
                                                  Arg.Is("lib"),
                                                  Arg.Is("v1"),
                                                  Arg.Any<string>(),
                                                  Arg.Any<CancellationToken>())
                .Returns(EmptyResult with { Versions = 1 });

        var json = await MutationTools.DeleteVersion(Substitute.For<RepositoryFactory>([null!]),
                                                      InlineRunner(),
                                                      deletion,
                                                      "lib",
                                                      "v1",
                                                      dryRun: false,
                                                      profile: null,
                                                      TestContext.Current.CancellationToken);

        Assert.Contains("\"Status\": \"Queued\"", json);
        await deletion.Received(1)
                      .DeleteVersionPreservingJobAsync(Arg.Is<string?>(value => value == null),
                                                       Arg.Is("lib"),
                                                       Arg.Is("v1"),
                                                       Arg.Any<string>(),
                                                       Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletedIndexIsNoLongerReturnedByInMemoryVectorSearch()
    {
        var provider = new InMemoryBruteForceVectorSearch();
        var chunk = new DocChunk
                        {
                            Id = "c1", LibraryId = "lib", Version = "v1", PageUrl = "https://example.test",
                            PageTitle = "title", Category = DocCategory.HowTo, Content = "content",
                            Embedding = [1f, 0f]
                        };
        await provider.IndexChunksAsync(null, "lib", "v1", [chunk], TestContext.Current.CancellationToken);
        var filter = new VectorSearchFilter { LibraryId = "lib", Version = "v1" };
        Assert.Single(await provider.SearchAsync([1f, 0f], filter, ct: TestContext.Current.CancellationToken));

        await provider.RemoveIndexAsync(null, "lib", "v1", TestContext.Current.CancellationToken);

        Assert.Empty(await provider.SearchAsync([1f, 0f], filter, ct: TestContext.Current.CancellationToken));
    }

    private static (LibraryDeletionService Service, Repositories Repos, IVectorSearchProvider Vector) BuildService()
    {
        var repos = new Repositories();
        var factory = Substitute.For<RepositoryFactory>([null!]);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(repos.Library);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(repos.Chunks);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(repos.Pages);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(repos.Profiles);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(repos.Indexes);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(repos.Bm25);
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(repos.Excluded);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(repos.Audit);
        factory.GetDiffRepository(Arg.Any<string?>()).Returns(repos.Diffs);
        factory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(repos.SourceDocuments);
        factory.GetSubjectCatalogRepository(Arg.Any<string?>()).Returns(repos.SubjectCatalogs);
        factory.GetSubjectAssignmentRepository(Arg.Any<string?>()).Returns(repos.SubjectAssignments);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(repos.Jobs);
        factory.GetProjectProfileRepository(Arg.Any<string?>()).Returns(repos.ProjectProfiles);
        factory.GetLibraryIngestionModeRepository(Arg.Any<string?>()).Returns(repos.Modes);
        repos.SourceDocuments.TryUpdateDirectoryPublicationAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                                  Arg.Any<string?>(),
                                                                  Arg.Any<DateTime?>(),
                                                                  Arg.Any<string?>(),
                                                                  Arg.Any<CancellationToken>())
             .Returns(true);
        repos.Modes.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns((LibraryIngestionModeRecord?)null);
        repos.Modes.GetLibraryDataEvidenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(new LibraryIngestionDataEvidence(HasLibraryRecord: true,
                                                       HasDirectoryDefinition: false,
                                                       HasDocumentLifecycleData: false,
                                                       HasChildContentData: false,
                                                       HasOperationalHistory: false));
        repos.Modes.TryAcquireAsync(Arg.Any<string>(),
                                    Arg.Any<LibraryIngestionMode>(),
                                    Arg.Any<string>(),
                                    Arg.Any<DateTime>(),
                                    Arg.Any<DateTime>(),
                                    Arg.Any<CancellationToken>())
             .Returns(call => new LibraryIngestionModeRecord
                                  {
                                      Id = call.ArgAt<string>(0),
                                      Mode = call.ArgAt<LibraryIngestionMode>(1),
                                      OwnershipState = LibraryIngestionOwnershipState.Reserved,
                                      LeaseOwnerToken = call.ArgAt<string>(2),
                                      LeaseExpiresAtUtc = call.ArgAt<DateTime>(4),
                                      ReservedAtUtc = call.ArgAt<DateTime>(3),
                                      UpdatedAtUtc = call.ArgAt<DateTime>(3)
                                  });
        repos.Modes.TryRenewAsync(Arg.Any<string>(),
                                  Arg.Any<LibraryIngestionMode>(),
                                  Arg.Any<string>(),
                                  Arg.Any<DateTime>(),
                                  Arg.Any<DateTime>(),
                                  Arg.Any<CancellationToken>())
             .Returns(true);
        repos.Modes.TryCommitAsync(Arg.Any<string>(),
                                   Arg.Any<LibraryIngestionMode>(),
                                   Arg.Any<string>(),
                                   Arg.Any<DateTime>(),
                                   Arg.Any<CancellationToken>())
             .Returns(true);
        repos.Modes.TryReleaseAsync(Arg.Any<string>(),
                                    Arg.Any<LibraryIngestionMode>(),
                                    Arg.Any<string>(),
                                    Arg.Any<DateTime>(),
                                    Arg.Any<CancellationToken>())
             .Returns(true);
        repos.Modes.TryDeleteOwnershipAsync(Arg.Any<string>(),
                                            Arg.Any<LibraryIngestionMode>(),
                                            Arg.Any<string>(),
                                            Arg.Any<CancellationToken>())
             .Returns(true);
        repos.Library.GetVersionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionRecord>());
        repos.Pages.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        repos.Chunks.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        repos.Profiles.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        repos.Indexes.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        repos.Bm25.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        repos.Excluded.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        repos.Audit.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        repos.Diffs.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        repos.SourceDocuments.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        var vector = Substitute.For<IVectorSearchProvider>();
        var modes = new LibraryIngestionModeLeaseManager(factory, TimeProvider.System);
        return (new LibraryDeletionService(factory, vector, modes), repos, vector);
    }

    private static async Task AssertNoVersionMutationsAsync(Repositories repos,
                                                             IVectorSearchProvider vector)
    {
        await repos.Chunks.DidNotReceive()
                   .DeleteChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.Pages.DidNotReceive()
                   .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SourceDocuments.DidNotReceive()
                   .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SourceDocuments.DidNotReceive()
                   .DeleteLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SourceDocuments.DidNotReceive()
                   .TryDeleteLeasedDirectoryDefinitionAsync(Arg.Any<IDirectoryPublicationLease>(),
                                                             Arg.Any<CancellationToken>());
        await repos.SubjectAssignments.DidNotReceive()
                   .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SubjectCatalogs.DidNotReceive()
                   .DeleteCandidateScanRunAsync(Arg.Any<string>(),
                                                Arg.Any<string>(),
                                                Arg.Any<string?>(),
                                                Arg.Any<CancellationToken>());
        await repos.Diffs.DidNotReceive()
                   .DeleteVersionAsync(Arg.Any<string>(),
                                       Arg.Any<string>(),
                                       Arg.Any<CancellationToken>());
        await repos.Jobs.DidNotReceive()
                   .DeleteManyAsync(Arg.Any<JobType?>(),
                                    Arg.Any<JobStatus?>(),
                                    Arg.Any<string?>(),
                                    Arg.Any<string?>(),
                                    Arg.Any<DateTime?>(),
                                    Arg.Any<CancellationToken>());
        await vector.DidNotReceive()
                    .RemoveIndexAsync(Arg.Any<string?>(),
                                      Arg.Any<string>(),
                                      Arg.Any<string>(),
                                      Arg.Any<CancellationToken>());
        await repos.Library.DidNotReceive()
                   .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task AssertNoLibraryMutationsAsync(Repositories repos,
                                                             IVectorSearchProvider vector)
    {
        await AssertNoVersionMutationsAsync(repos, vector);
        await repos.SourceDocuments.DidNotReceive()
                   .DeleteLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SubjectAssignments.DidNotReceive()
                   .DeleteLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.SubjectCatalogs.DidNotReceive()
                   .DeleteLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.Diffs.DidNotReceive()
                   .DeleteLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repos.ProjectProfiles.DidNotReceive()
                   .RemoveIngestedPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await vector.DidNotReceive()
                    .RemoveLibraryIndexesAsync(Arg.Any<string?>(),
                                                Arg.Any<string>(),
                                                Arg.Any<CancellationToken>());
        await repos.Library.DidNotReceive()
                   .DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static void ConfigureDeleteEvents(Repositories repos, List<string> events)
    {
        repos.Chunks.DeleteChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("chunks"); return 1L; });
        repos.Pages.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("pages"); return 1L; });
        repos.Profiles.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("profiles"); return 1L; });
        repos.Indexes.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("indexes"); return 1L; });
        repos.Bm25.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("bm25"); return 1L; });
        repos.Excluded.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("excluded"); return 1L; });
        repos.Audit.DeleteByLibraryVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("audit"); return 1L; });
        repos.Jobs.DeleteManyAsync(jobType: null,
                                   status: null,
                                   libraryId: Arg.Any<string?>(),
                                   version: Arg.Is<string?>(value => value != null),
                                   completedBefore: null,
                                   ct: Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("jobs"); return 1L; });
        repos.Jobs.DeleteManyAsync(jobType: null,
                                   status: null,
                                   libraryId: Arg.Any<string?>(),
                                   version: Arg.Is<string?>(value => value == null),
                                   completedBefore: null,
                                   ct: Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("jobs-library"); return 0L; });
        repos.ProjectProfiles.RemoveIngestedPackageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("project-profiles-library"); return 1L; });
        repos.Diffs.DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("diffs"); return 1L; });
        repos.SourceDocuments.GetRevisionsAsync(Arg.Any<string>(),
                                                Arg.Any<string>(),
                                                Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult<IReadOnlyList<DocumentRevisionRecord>>(
                          [Revision(call.ArgAt<string>(1))]));
        repos.SubjectAssignments.DeleteVersionAsync(Arg.Any<string>(),
                                                     Arg.Any<string>(),
                                                     Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("subject-assignments"); return 1L; });
        repos.SubjectCatalogs.DeleteCandidateScanRunAsync(Arg.Any<string>(),
                                                          Arg.Any<string>(),
                                                          Arg.Any<string?>(),
                                                          Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("subject-catalogs"); return 1L; });
        repos.SourceDocuments.DeleteVersionAsync(Arg.Any<string>(),
                                                 Arg.Any<string>(),
                                                 Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("documents"); return 1L; });
        repos.SourceDocuments.DeleteLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("documents-library"); return 0L; });
        repos.SubjectAssignments.DeleteLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("subject-assignments-library"); return 0L; });
        repos.SubjectCatalogs.DeleteLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("subject-catalogs-library"); return 0L; });
        repos.Diffs.DeleteLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ => { events.Add("diffs-library"); return 0L; });
    }

    private static DocumentRevisionRecord Revision(string version) => new()
        {
            Id = $"revision-{version}",
            DocumentId = "document",
            LibraryId = "lib",
            Version = version,
            ScanRunId = $"scan-{version}",
            State = DocumentRevisionState.Candidate,
            AcquiredAtUtc = DateTime.UtcNow,
            OriginalArtifactHash = "hash",
            OriginalByteLength = 1,
            OriginalMediaType = "text/plain"
        };

    private static LibraryVersionRecord Version(string version) => new()
        {
            Id = $"lib/{version}", LibraryId = "lib", Version = version, ScrapedAt = DateTime.UtcNow,
            PageCount = 1, ChunkCount = 1, EmbeddingProviderId = "p", EmbeddingModelName = "m",
            EmbeddingDimensions = 2, PublicationState = VersionPublicationState.Published
        };

    private static DirectoryLibraryDefinition DirectoryDefinition() => new()
        {
            Id = "lib",
            RootPath = "C:\\manuals",
            Recursive = true,
            BindingStatus = DirectoryLibraryBindingStatus.Bound,
            RegisteredAtUtc = DateTime.UtcNow,
            RegistrationRevision = 7,
            RegistrationIncarnationId = "registration-incarnation",
            LastPublishedVersion = "v1"
        };

    private static IBackgroundJobRunner InlineRunner()
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>())
              .Returns(async call =>
                   {
                       var record = call.Arg<BackgroundJobRecord>()!;
                       var action = call.Arg<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>()!;
                       await action(record, null, CancellationToken.None);
                       return record.Id;
                   });
        return runner;
    }

    private sealed class Repositories
    {
        public ILibraryRepository Library { get; } = Substitute.For<ILibraryRepository>();
        public IChunkRepository Chunks { get; } = Substitute.For<IChunkRepository>();
        public IPageRepository Pages { get; } = Substitute.For<IPageRepository>();
        public ILibraryProfileRepository Profiles { get; } = Substitute.For<ILibraryProfileRepository>();
        public ILibraryIndexRepository Indexes { get; } = Substitute.For<ILibraryIndexRepository>();
        public IBm25ShardRepository Bm25 { get; } = Substitute.For<IBm25ShardRepository>();
        public IExcludedSymbolsRepository Excluded { get; } = Substitute.For<IExcludedSymbolsRepository>();
        public IScrapeAuditRepository Audit { get; } = Substitute.For<IScrapeAuditRepository>();
        public IDiffRepository Diffs { get; } = Substitute.For<IDiffRepository>();
        public ISourceDocumentRepository SourceDocuments { get; } = Substitute.For<ISourceDocumentRepository>();
        public ISubjectCatalogRepository SubjectCatalogs { get; } = Substitute.For<ISubjectCatalogRepository>();
        public ISubjectAssignmentRepository SubjectAssignments { get; } =
            Substitute.For<ISubjectAssignmentRepository>();
        public IJobRepository Jobs { get; } = Substitute.For<IJobRepository>();
        public IProjectProfileRepository ProjectProfiles { get; } =
            Substitute.For<IProjectProfileRepository>();
        public ILibraryIngestionModeRepository Modes { get; } =
            Substitute.For<ILibraryIngestionModeRepository>();
    }

    private sealed class ScriptedDirectoryPublicationLease : IDirectoryPublicationLease
    {
        internal ScriptedDirectoryPublicationLease(IReadOnlyList<bool> renewalResults)
        {
            mRenewalResults = new Queue<bool>(renewalResults);
        }

        private readonly Queue<bool> mRenewalResults;
        private readonly CancellationTokenSource mOwnershipLost = new();

        public string LibraryId => "lib";

        public string ScanRunId => "delete-version";

        public string? RegistrationIncarnationId => "registration-incarnation";

        public long RegistrationRevision => 7;

        public CancellationToken OwnershipLostToken => mOwnershipLost.Token;

        public int RenewalCount { get; private set; }

        public ValueTask<bool> TryRenewAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RenewalCount++;
            bool result = mRenewalResults.Count == 0 || mRenewalResults.Dequeue();
            if (!result)
                LoseOwnership();
            return ValueTask.FromResult(result);
        }

        internal void LoseOwnership()
        {
            mOwnershipLost.Cancel();
        }

        public ValueTask DisposeAsync()
        {
            mOwnershipLost.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static readonly LibraryDeletionResult EmptyResult = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}
