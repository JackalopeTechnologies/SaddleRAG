// MonitorDeleteCascadeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Embedding;
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
        Assert.Equal("metadata", events[^1]);
        Assert.Equal("vector", events[^2]);
        Assert.Equal(12, events.Count);
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
        await repos.Pages.Received(1).DeleteAsync("lib", "v1", CancellationToken.None);
        await repos.SourceDocuments.Received(1).DeleteVersionAsync("lib", "v1", CancellationToken.None);
        await repos.SubjectAssignments.Received(1)
                   .DeleteScanRunAsync("lib", "scan-v1", CancellationToken.None);
        await repos.SubjectCatalogs.Received(1)
                   .DeleteCandidateScanRunAsync("lib", "scan-v1", "v1", CancellationToken.None);
        await vector.Received(1).RemoveIndexAsync(null, "lib", "v1", CancellationToken.None);
        await repos.Library.DidNotReceive()
                   .DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        Assert.Equal("vector-library", events[^2]);
        Assert.Equal("metadata-library", events[^1]);
        await repos.SourceDocuments.Received(1)
                   .DeleteLibraryAsync("lib", Arg.Any<CancellationToken>());
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
        deletion.DeleteVersionAsync(null, "lib", "v1", Arg.Any<CancellationToken>())
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
                      .DeleteVersionAsync(null, "lib", "v1", Arg.Any<CancellationToken>());
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
        factory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(repos.SourceDocuments);
        factory.GetSubjectCatalogRepository(Arg.Any<string?>()).Returns(repos.SubjectCatalogs);
        factory.GetSubjectAssignmentRepository(Arg.Any<string?>()).Returns(repos.SubjectAssignments);
        var vector = Substitute.For<IVectorSearchProvider>();
        return (new LibraryDeletionService(factory, vector), repos, vector);
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
        repos.SourceDocuments.GetRevisionsAsync(Arg.Any<string>(),
                                                Arg.Any<string>(),
                                                Arg.Any<CancellationToken>())
             .Returns(call => Task.FromResult<IReadOnlyList<DocumentRevisionRecord>>(
                          [Revision(call.ArgAt<string>(1))]));
        repos.SubjectAssignments.DeleteScanRunAsync(Arg.Any<string>(),
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
        public ISourceDocumentRepository SourceDocuments { get; } = Substitute.For<ISourceDocumentRepository>();
        public ISubjectCatalogRepository SubjectCatalogs { get; } = Substitute.For<ISubjectCatalogRepository>();
        public ISubjectAssignmentRepository SubjectAssignments { get; } =
            Substitute.For<ISubjectAssignmentRepository>();
    }

    private static readonly LibraryDeletionResult EmptyResult = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}
