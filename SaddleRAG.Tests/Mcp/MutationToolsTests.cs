// MutationToolsTests.cs
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

public sealed class MutationToolsTests
{
    [Fact]
    public async Task RenameLibraryDryRunReportsOutcomeWithoutWriting()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var factory = Substitute.For<RepositoryFactory>([null!]);

        libraryRepo.GetLibraryAsync("old", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "old",
                                    Name = "old",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = ["1.0"]
                                }
                           );
        libraryRepo.GetLibraryAsync("new", Arg.Any<CancellationToken>())
                   .Returns((LibraryRecord?) null);

        factory.GetLibraryRepository(profile: null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeNoopRunner(),
                                                     Substitute.For<ILibraryRenameService>(),
                                                     "old",
                                                     "new",
                                                     dryRun: true,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Outcome\": \"Renamed\"", json);
        await libraryRepo.DidNotReceive()
                         .RenameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameLibraryDryRunReportsNotFoundWhenMissing()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var factory = Substitute.For<RepositoryFactory>([null!]);

        libraryRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>())
                   .Returns((LibraryRecord?) null);

        factory.GetLibraryRepository(profile: null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeNoopRunner(),
                                                     Substitute.For<ILibraryRenameService>(),
                                                     "missing",
                                                     "new",
                                                     dryRun: true,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Outcome\": \"NotFound\"", json);
    }

    [Fact]
    public async Task RenameLibraryApplyQueuesJobAndCallsRenameAsync()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var renameService = Substitute.For<ILibraryRenameService>();
        BackgroundJobRecord? completed = null;

        renameService.RenameLibraryAsync(null, "old", "new", Arg.Any<CancellationToken>())
                     .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Renamed,
                                                        new RenameLibraryResult(Libraries: 1,
                                                                                Versions: 1,
                                                                                Chunks: 100,
                                                                                Pages: 50,
                                                                                Profiles: 1,
                                                                                Indexes: 1,
                                                                                Bm25Shards: 1,
                                                                                ExcludedSymbols: 5,
                                                                                ScrapeJobs: 3)));

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeInlineRunner(record => completed = record),
                                                     renameService,
                                                     "old",
                                                     "new",
                                                     dryRun: false,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await renameService.Received(requiredNumberOfCalls: 1)
                           .RenameLibraryAsync(null, "old", "new", Arg.Any<CancellationToken>());
        Assert.Equal("new", Assert.IsType<BackgroundJobRecord>(completed).LibraryId);
    }

    [Fact]
    public async Task RenameLibraryApplyQueuesJobEvenOnCollision()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var renameService = Substitute.For<ILibraryRenameService>();
        BackgroundJobRecord? completed = null;

        renameService.RenameLibraryAsync(null, "old", "new", Arg.Any<CancellationToken>())
                     .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Collision, Counts: null));

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeInlineRunner(record => completed = record),
                                                     renameService,
                                                     "old",
                                                     "new",
                                                     dryRun: false,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await renameService.Received(requiredNumberOfCalls: 1)
                           .RenameLibraryAsync(null, "old", "new", Arg.Any<CancellationToken>());
        Assert.Equal("old", Assert.IsType<BackgroundJobRecord>(completed).LibraryId);
    }

    [Fact]
    public async Task DeleteVersionDryRunReportsCascadeWithoutWriting()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var jobRepo = Substitute.For<IJobRepository>();
        var projectProfiles = Substitute.For<IProjectProfileRepository>();
        var factory = Substitute.For<RepositoryFactory>([null!]);

        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        factory.GetProjectProfileRepository(Arg.Any<string?>()).Returns(projectProfiles);
        jobRepo.CountDeleteCandidatesAsync(jobType: null,
                                            status: null,
                                            libraryId: "foo",
                                            version: null,
                                            completedBefore: null,
                                            ct: Arg.Any<CancellationToken>())
               .Returns(7L);
        projectProfiles.CountIngestedPackageReferencesAsync("foo", Arg.Any<CancellationToken>())
                       .Returns(2L);

        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "foo",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = ["1.0"]
                                }
                           );
        chunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(returnThis: 123);
        pageRepo.GetPageCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(returnThis: 45);

        var json = await MutationTools.DeleteVersion(factory,
                                                     MakeNoopRunner(),
                                                     Substitute.For<ILibraryDeletionService>(),
                                                     "foo",
                                                     "1.0",
                                                     dryRun: true,
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Chunks\": 123", json);
        Assert.Contains("\"Pages\": 45", json);
        Assert.Contains("\"Jobs\": 7", json);
        Assert.Contains("\"ProjectProfiles\": 2", json);
        await chunkRepo.DidNotReceive()
                       .DeleteChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteVersionApplyQueuesJobAndDelegatesToSharedCascade()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var deletionService = Substitute.For<ILibraryDeletionService>();

        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excludedRepo);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);

        deletionService.DeleteVersionPreservingJobAsync(Arg.Is<string?>(value => value == null),
                                                         Arg.Is("foo"),
                                                         Arg.Is("1.0"),
                                                         Arg.Any<string>(),
                                                         Arg.Any<CancellationToken>())
                       .Returns(new LibraryDeletionResult(0, 1, 0, 0, 0, 0, 0, 0, 0, "0.9"));

        var json = await MutationTools.DeleteVersion(factory,
                                                     MakeInlineRunner(),
                                                     deletionService,
                                                     "foo",
                                                     "1.0",
                                                     dryRun: false,
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await deletionService.Received(requiredNumberOfCalls: 1)
                             .DeleteVersionPreservingJobAsync(Arg.Is<string?>(value => value == null),
                                                              Arg.Is("foo"),
                                                              Arg.Is("1.0"),
                                                              Arg.Any<string>(),
                                                              Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteLibraryDryRunAggregatesAcrossAllVersionsWithoutWriting()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var jobRepo = Substitute.For<IJobRepository>();
        var projectProfiles = Substitute.For<IProjectProfileRepository>();
        var factory = Substitute.For<RepositoryFactory>([null!]);

        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        factory.GetProjectProfileRepository(Arg.Any<string?>()).Returns(projectProfiles);
        jobRepo.CountDeleteCandidatesAsync(jobType: null,
                                            status: null,
                                            libraryId: "foo",
                                            version: null,
                                            completedBefore: null,
                                            ct: Arg.Any<CancellationToken>())
               .Returns(8L);
        projectProfiles.CountIngestedPackageReferencesAsync("foo", Arg.Any<CancellationToken>())
                       .Returns(3L);

        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "foo",
                                    Hint = "h",
                                    CurrentVersion = "2.0",
                                    AllVersions = ["1.0", "2.0"]
                                }
                           );
        chunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(returnThis: 50);
        chunkRepo.GetChunkCountAsync("foo", "2.0", Arg.Any<CancellationToken>()).Returns(returnThis: 100);
        pageRepo.GetPageCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(returnThis: 10);
        pageRepo.GetPageCountAsync("foo", "2.0", Arg.Any<CancellationToken>()).Returns(returnThis: 20);

        var json = await MutationTools.DeleteLibrary(factory,
                                                     MakeNoopRunner(),
                                                     Substitute.For<ILibraryDeletionService>(),
                                                     "foo",
                                                     dryRun: true,
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"Versions\":", json);
        Assert.Contains("\"Chunks\": 150", json);
        Assert.Contains("\"Pages\": 30", json);
        Assert.Contains("\"Jobs\": 8", json);
        Assert.Contains("\"ProjectProfiles\": 3", json);
    }

    [Fact]
    public async Task DeleteLibraryApplyQueuesJobAndDelegatesToSharedCascade()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var deletionService = Substitute.For<ILibraryDeletionService>();

        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excludedRepo);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);

        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "foo",
                                    Hint = "h",
                                    CurrentVersion = "2.0",
                                    AllVersions = ["1.0", "2.0"]
                                }
                           );
        deletionService.DeleteLibraryPreservingJobAsync(Arg.Is<string?>(value => value == null),
                                                         Arg.Is("foo"),
                                                         Arg.Any<string>(),
                                                         Arg.Any<CancellationToken>())
                       .Returns(new LibraryDeletionResult(1, 2, 0, 0, 0, 0, 0, 0, 0));

        var json = await MutationTools.DeleteLibrary(factory,
                                                     MakeInlineRunner(),
                                                     deletionService,
                                                     "foo",
                                                     dryRun: false,
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await deletionService.Received(requiredNumberOfCalls: 1)
                             .DeleteLibraryPreservingJobAsync(Arg.Is<string?>(value => value == null),
                                                              Arg.Is("foo"),
                                                              Arg.Any<string>(),
                                                              Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameVersionDryRunReportsCountsWithoutWriting()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var factory = Substitute.For<RepositoryFactory>([null!]);

        factory.GetLibraryRepository(profile: null).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);

        libraryRepo.GetVersionAsync("scichart-wpf", "current", Arg.Any<CancellationToken>())
                   .Returns(MakeVersionRecord("scichart-wpf", "current"));
        libraryRepo.GetVersionAsync("scichart-wpf", "v8", Arg.Any<CancellationToken>())
                   .Returns((LibraryVersionRecord?) null);
        libraryRepo.GetLibraryAsync("scichart-wpf", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord { Id = "scichart-wpf", Name = "s", Hint = "h",
                                                CurrentVersion = "current", AllVersions = ["current"] });
        chunkRepo.GetChunkCountAsync("scichart-wpf", "current", Arg.Any<CancellationToken>()).Returns(48734);
        pageRepo.GetPageCountAsync("scichart-wpf", "current", Arg.Any<CancellationToken>()).Returns(20636);

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeNoopRunner(),
                                                     Substitute.For<ILibraryRenameService>(),
                                                     "scichart-wpf",
                                                     newId: null, version: "current", newVersion: "v8",
                                                     dryRun: true, profile: null,
                                                     TestContext.Current.CancellationToken);

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Outcome\": \"Renamed\"", json);
        Assert.Contains("\"Chunks\": 48734", json);
        Assert.Contains("\"CurrentVersionRepointedTo\": \"v8\"", json);
        await libraryRepo.DidNotReceive()
                         .RenameVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                                             Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameVersionApplyQueuesJobAndCallsRenameVersionAsync()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var renameService = Substitute.For<ILibraryRenameService>();
        BackgroundJobRecord? completed = null;
        renameService.RenameVersionAsync(null,
                                         "scichart-wpf",
                                         "current",
                                         "v8",
                                         Arg.Any<CancellationToken>())
                     .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Renamed,
                                                        new RenameLibraryResult(1, 1, 48734, 20636, 1, 1, 4, 0, 0)));

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeInlineRunner(record => completed = record),
                                                     renameService,
                                                     "scichart-wpf",
                                                     newId: null, version: "current", newVersion: "v8",
                                                     dryRun: false, profile: null,
                                                     TestContext.Current.CancellationToken);

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await renameService.Received(requiredNumberOfCalls: 1)
                           .RenameVersionAsync(null,
                                               "scichart-wpf",
                                               "current",
                                               "v8",
                                               Arg.Any<CancellationToken>());
        BackgroundJobRecord completedRecord = Assert.IsType<BackgroundJobRecord>(completed);
        Assert.Equal("scichart-wpf", completedRecord.LibraryId);
        Assert.Equal("v8", completedRecord.Version);
    }

    [Theory]
    // both modes
    [InlineData("new", "current", "v8")]
    // version without newVersion
    [InlineData(null, "current", null)]
    // newVersion without version
    [InlineData(null, null, "v8")]
    // identical
    [InlineData(null, "v8", "v8")]
    // slash in newVersion
    [InlineData(null, "current", "a/b")]
    // nothing
    [InlineData(null, null, null)]
    public async Task RenameRejectsInvalidArgumentCombos(string? newId, string? version, string? newVersion)
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeNoopRunner(),
                                                     Substitute.For<ILibraryRenameService>(),
                                                     "lib",
                                                     newId, version, newVersion,
                                                     dryRun: true, profile: null,
                                                     TestContext.Current.CancellationToken);

        Assert.Contains("\"Error\":", json);
    }

    private static LibraryVersionRecord MakeVersionRecord(string lib, string ver) =>
        new()
        {
            Id = $"{lib}/{ver}",
            LibraryId = lib,
            Version = ver,
            ScrapedAt = DateTime.UtcNow,
            PageCount = 1,
            ChunkCount = 1,
            EmbeddingProviderId = "onnx",
            EmbeddingModelName = "nomic-embed-text-v1.5",
            EmbeddingDimensions = 768
        };

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

    private static IBackgroundJobRunner MakeInlineRunner(Action<BackgroundJobRecord>? onCompleted = null)
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>()
                         )
              .Returns(async callInfo =>
                       {
                           var record = callInfo.Arg<BackgroundJobRecord>()!;
                           var execute = callInfo
                               .Arg<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>()!;
                           await execute(record, arg2: null, CancellationToken.None);
                           onCompleted?.Invoke(record);
                           return record.Id;
                       }
                      );
        return runner;
    }
}
