// OrphanCleanupToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class OrphanCleanupToolsTests
{
    private sealed record Fixture(
        RepositoryFactory Factory,
        ILibraryRepository LibraryRepo,
        IPageRepository PageRepo,
        IChunkRepository ChunkRepo,
        ILibraryProfileRepository ProfileRepo,
        ILibraryIndexRepository IndexRepo,
        IBm25ShardRepository Bm25Repo,
        IExcludedSymbolsRepository ExcludedRepo,
        IScrapeAuditRepository AuditRepo)
    {
        public void SeedLibraries(params LibraryRecord[] libraries)
        {
            LibraryRepo.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
                       .Returns(libraries.ToList());
        }

        public void SeedPagePairs(params LibraryVersionKey[] pairs) =>
            PageRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                    .Returns(pairs.ToList());

        public void SeedChunkPairs(params LibraryVersionKey[] pairs) =>
            ChunkRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                     .Returns(pairs.ToList());

        public void SeedProfilePairs(params LibraryVersionKey[] pairs) =>
            ProfileRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                       .Returns(pairs.ToList());

        public void SeedIndexPairs(params LibraryVersionKey[] pairs) =>
            IndexRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                     .Returns(pairs.ToList());

        public void SeedShardPairs(params LibraryVersionKey[] pairs) =>
            Bm25Repo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                    .Returns(pairs.ToList());

        public void SeedExcludedPairs(params LibraryVersionKey[] pairs) =>
            ExcludedRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                        .Returns(pairs.ToList());

        public void SeedAuditPairs(params LibraryVersionKey[] pairs) =>
            AuditRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                     .Returns(pairs.ToList());
    }

    [Fact]
    public async Task DryRunReportsOrphansAcrossAllCollectionsWithoutWriting()
    {
        var fixture = BuildFixture();
        fixture.SeedLibraries(new LibraryRecord
                                  {
                                      Id = "good-lib",
                                      Name = "good-lib",
                                      Hint = "h",
                                      CurrentVersion = "1.0",
                                      AllVersions = ["1.0"]
                                  }
                             );
        fixture.SeedPagePairs(new LibraryVersionKey("good-lib", "1.0"),
                              new LibraryVersionKey("orphan-lib", "1.0")
                             );
        fixture.SeedChunkPairs(new LibraryVersionKey("good-lib", "1.0"),
                               new LibraryVersionKey("orphan-lib", "1.0")
                              );
        fixture.SeedProfilePairs(new LibraryVersionKey("orphan-lib", "1.0"));
        fixture.SeedIndexPairs(new LibraryVersionKey("orphan-lib", "1.0"));
        fixture.SeedShardPairs(new LibraryVersionKey("orphan-lib", "1.0"));
        fixture.SeedExcludedPairs(new LibraryVersionKey("orphan-lib", "1.0"));
        fixture.SeedAuditPairs(new LibraryVersionKey("orphan-lib", "1.0"));

        var json = await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                           MakeNoopRunner(),
                                                           library: null,
                                                           version: null,
                                                           dryRun: true,
                                                           profile: null,
                                                           TestContext.Current.CancellationToken
                                                          );

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"OrphanedPairs\": 1", json);
        Assert.Contains("\"LibraryId\": \"orphan-lib\"", json);
        Assert.Contains("\"Pages\": 1", json);
        Assert.Contains("\"Chunks\": 1", json);
        Assert.Contains("\"Profiles\": 1", json);
        Assert.Contains("\"Indexes\": 1", json);
        Assert.Contains("\"Bm25Shards\": 1", json);
        Assert.Contains("\"ExcludedSymbols\": 1", json);
        Assert.Contains("\"AuditEntries\": 1", json);
        await fixture.PageRepo.DidNotReceive()
                     .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.ChunkRepo.DidNotReceive()
                     .DeleteChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DryRunReportsZeroOrphansWhenAllChildPairsHaveParents()
    {
        var fixture = BuildFixture();
        fixture.SeedLibraries(new LibraryRecord
                                  {
                                      Id = "lib-a",
                                      Name = "lib-a",
                                      Hint = "h",
                                      CurrentVersion = "2.0",
                                      AllVersions = ["1.0", "2.0"]
                                  }
                             );
        fixture.SeedPagePairs(new LibraryVersionKey("lib-a", "1.0"),
                              new LibraryVersionKey("lib-a", "2.0")
                             );
        fixture.SeedChunkPairs(new LibraryVersionKey("lib-a", "2.0"));

        var json = await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                           MakeNoopRunner(),
                                                           library: null,
                                                           version: null,
                                                           dryRun: true,
                                                           profile: null,
                                                           TestContext.Current.CancellationToken
                                                          );

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"OrphanedPairs\": 0", json);
    }

    [Fact]
    public async Task DryRunHonoursLibraryFilter()
    {
        var fixture = BuildFixture();
        fixture.SeedLibraries();
        fixture.SeedPagePairs(new LibraryVersionKey("orphan-a", "1.0"),
                              new LibraryVersionKey("orphan-b", "1.0")
                             );

        var json = await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                           MakeNoopRunner(),
                                                           "orphan-b",
                                                           version: null,
                                                           dryRun: true,
                                                           profile: null,
                                                           TestContext.Current.CancellationToken
                                                          );

        Assert.Contains("\"OrphanedPairs\": 1", json);
        Assert.Contains("\"LibraryId\": \"orphan-b\"", json);
        Assert.DoesNotContain("\"LibraryId\": \"orphan-a\"", json);
    }

    [Fact]
    public async Task DryRunHonoursVersionFilter()
    {
        var fixture = BuildFixture();
        fixture.SeedLibraries();
        fixture.SeedPagePairs(new LibraryVersionKey("orphan-lib", "1.0"),
                              new LibraryVersionKey("orphan-lib", "2.0")
                             );

        var json = await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                           MakeNoopRunner(),
                                                           library: null,
                                                           "2.0",
                                                           dryRun: true,
                                                           profile: null,
                                                           TestContext.Current.CancellationToken
                                                          );

        Assert.Contains("\"OrphanedPairs\": 1", json);
        Assert.Contains("\"Version\": \"2.0\"", json);
        Assert.DoesNotContain("\"Version\": \"1.0\"", json);
    }

    [Fact]
    public async Task DryRunTreatsLibraryRowWithMissingVersionAsOrphan()
    {
        var fixture = BuildFixture();
        fixture.SeedLibraries(new LibraryRecord
                                  {
                                      Id = "partial",
                                      Name = "partial",
                                      Hint = "h",
                                      CurrentVersion = "2.0",
                                      AllVersions = ["2.0"]
                                  }
                             );
        fixture.SeedPagePairs(new LibraryVersionKey("partial", "2.0"),
                              new LibraryVersionKey("partial", "1.0")
                             );

        var json = await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                           MakeNoopRunner(),
                                                           library: null,
                                                           version: null,
                                                           dryRun: true,
                                                           profile: null,
                                                           TestContext.Current.CancellationToken
                                                          );

        Assert.Contains("\"OrphanedPairs\": 1", json);
        Assert.Contains("\"LibraryId\": \"partial\"", json);
        Assert.Contains("\"Version\": \"1.0\"", json);
    }

    [Fact]
    public async Task ApplyQueuesJobAndDeletesEachOrphanFromEveryCollection()
    {
        var fixture = BuildFixture();
        fixture.SeedLibraries();
        var orphan = new LibraryVersionKey("actipro-wpf", "25.1");
        fixture.SeedPagePairs(orphan);
        fixture.SeedChunkPairs(orphan);
        fixture.SeedProfilePairs(orphan);
        fixture.SeedIndexPairs(orphan);
        fixture.SeedShardPairs(orphan);
        fixture.SeedExcludedPairs(orphan);
        fixture.SeedAuditPairs(orphan);

        fixture.PageRepo.DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>())
               .Returns(returnThis: 9_977L);
        fixture.ChunkRepo.DeleteChunksAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>())
               .Returns(returnThis: 25_984L);
        fixture.ProfileRepo.DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>())
               .Returns(returnThis: 1L);
        fixture.IndexRepo.DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>())
               .Returns(returnThis: 1L);
        fixture.Bm25Repo.DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>())
               .Returns(returnThis: 4L);
        fixture.ExcludedRepo.DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>())
               .Returns(returnThis: 17L);
        fixture.AuditRepo.DeleteByLibraryVersionAsync(orphan.LibraryId,
                                                      orphan.Version,
                                                      Arg.Any<CancellationToken>()
                                                     )
               .Returns(returnThis: 50L);

        var json = await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                           MakeInlineRunner(),
                                                           library: null,
                                                           version: null,
                                                           dryRun: false,
                                                           profile: null,
                                                           TestContext.Current.CancellationToken
                                                          );

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await fixture.PageRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>());
        await fixture.ChunkRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteChunksAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>());
        await fixture.ProfileRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>());
        await fixture.IndexRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>());
        await fixture.Bm25Repo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>());
        await fixture.ExcludedRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync(orphan.LibraryId, orphan.Version, Arg.Any<CancellationToken>());
        await fixture.AuditRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteByLibraryVersionAsync(orphan.LibraryId,
                                                  orphan.Version,
                                                  Arg.Any<CancellationToken>()
                                                 );
    }

    [Fact]
    public async Task ApplyDoesNotDeleteRowsForValidParents()
    {
        var fixture = BuildFixture();
        fixture.SeedLibraries(new LibraryRecord
                                  {
                                      Id = "valid",
                                      Name = "valid",
                                      Hint = "h",
                                      CurrentVersion = "1.0",
                                      AllVersions = ["1.0"]
                                  }
                             );
        fixture.SeedPagePairs(new LibraryVersionKey("valid", "1.0"),
                              new LibraryVersionKey("orphan", "1.0")
                             );
        fixture.SeedChunkPairs(new LibraryVersionKey("valid", "1.0"),
                               new LibraryVersionKey("orphan", "1.0")
                              );

        await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                MakeInlineRunner(),
                                                library: null,
                                                version: null,
                                                dryRun: false,
                                                profile: null,
                                                TestContext.Current.CancellationToken
                                               );

        await fixture.PageRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync("orphan", "1.0", Arg.Any<CancellationToken>());
        await fixture.PageRepo.DidNotReceive()
                     .DeleteAsync("valid", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.ChunkRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteChunksAsync("orphan", "1.0", Arg.Any<CancellationToken>());
        await fixture.ChunkRepo.DidNotReceive()
                     .DeleteChunksAsync("valid", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyHonoursLibraryFilterAndIgnoresUnscopedOrphans()
    {
        var fixture = BuildFixture();
        fixture.SeedLibraries();
        fixture.SeedPagePairs(new LibraryVersionKey("orphan-a", "1.0"),
                              new LibraryVersionKey("orphan-b", "1.0")
                             );

        await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                MakeInlineRunner(),
                                                "orphan-a",
                                                version: null,
                                                dryRun: false,
                                                profile: null,
                                                TestContext.Current.CancellationToken
                                               );

        await fixture.PageRepo.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync("orphan-a", "1.0", Arg.Any<CancellationToken>());
        await fixture.PageRepo.DidNotReceive()
                     .DeleteAsync("orphan-b", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static Fixture BuildFixture()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>([null]);

        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excludedRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);

        // Default empty pair lists so any test that doesn't seed a collection
        // implicitly contributes zero orphans.
        pageRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                .Returns(EmptyKeys);
        chunkRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                 .Returns(EmptyKeys);
        profileRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                   .Returns(EmptyKeys);
        indexRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                 .Returns(EmptyKeys);
        bm25Repo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                .Returns(EmptyKeys);
        excludedRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                    .Returns(EmptyKeys);
        auditRepo.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                 .Returns(EmptyKeys);

        return new Fixture(factory,
                           libraryRepo,
                           pageRepo,
                           chunkRepo,
                           profileRepo,
                           indexRepo,
                           bm25Repo,
                           excludedRepo,
                           auditRepo
                          );
    }

    private static IBackgroundJobRunner MakeNoopRunner()
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>()
                         )
              .Returns(Guid.NewGuid().ToString());
        return runner;
    }

    private static IBackgroundJobRunner MakeInlineRunner()
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>()
                         )
              .Returns(async callInfo =>
                       {
                           var record = callInfo.Arg<BackgroundJobRecord>();
                           var execute =
                               callInfo
                                   .Arg<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>();
                           await execute(record, arg2: null, CancellationToken.None);
                           return record.Id;
                       }
                      );
        return runner;
    }

    private static readonly IReadOnlyList<LibraryVersionKey> EmptyKeys = [];
}
