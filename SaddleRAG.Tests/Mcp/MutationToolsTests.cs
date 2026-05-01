// MutationToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        libraryRepo.GetLibraryAsync("old", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "old",
                                    Name = "old",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = new List<string> { "1.0" }
                                });
        libraryRepo.GetLibraryAsync("new", Arg.Any<CancellationToken>())
                   .Returns((LibraryRecord?) null);

        factory.GetLibraryRepository(null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeNoopRunner(),
                                                     library: "old",
                                                     newId: "new",
                                                     dryRun: true,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Outcome\": \"Renamed\"", json);
        await libraryRepo.DidNotReceive().RenameAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameLibraryDryRunReportsNotFoundWhenMissing()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        libraryRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>())
                   .Returns((LibraryRecord?) null);

        factory.GetLibraryRepository(null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeNoopRunner(),
                                                     library: "missing",
                                                     newId: "new",
                                                     dryRun: true,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Outcome\": \"NotFound\"", json);
    }

    [Fact]
    public async Task RenameLibraryApplyQueuesJobAndCallsRenameAsync()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        libraryRepo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
                   .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Renamed,
                                                     new RenameLibraryResult(1, 1, 100, 50, 1, 1, 1, 5, 3)));

        factory.GetLibraryRepository(null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeInlineRunner(),
                                                     library: "old",
                                                     newId: "new",
                                                     dryRun: false,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await libraryRepo.Received(1).RenameAsync("old", "new", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenameLibraryApplyQueuesJobEvenOnCollision()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        libraryRepo.RenameAsync("old", "new", Arg.Any<CancellationToken>())
                   .Returns(new RenameLibraryResponse(RenameLibraryOutcome.Collision, null));

        factory.GetLibraryRepository(null).Returns(libraryRepo);

        var json = await MutationTools.RenameLibrary(factory,
                                                     MakeInlineRunner(),
                                                     library: "old",
                                                     newId: "new",
                                                     dryRun: false,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
    }

    [Fact]
    public async Task DeleteVersionDryRunReportsCascadeWithoutWriting()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);

        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "foo",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = new List<string> { "1.0" }
                                });
        chunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(123);
        pageRepo.GetPageCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(45);

        var json = await MutationTools.DeleteVersion(factory,
                                                     MakeNoopRunner(),
                                                     library: "foo",
                                                     version: "1.0",
                                                     dryRun: true,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"Chunks\": 123", json);
        Assert.Contains("\"Pages\": 45", json);
        await chunkRepo.DidNotReceive().DeleteChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteVersionApplyQueuesJobAndCallsAllCollections()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profileRepo);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexRepo);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25Repo);
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excludedRepo);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);

        libraryRepo.DeleteVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
                   .Returns(new DeleteVersionResult(VersionsDeleted: 1, LibraryRowDeleted: false, CurrentVersionRepointedTo: "0.9"));

        var json = await MutationTools.DeleteVersion(factory,
                                                     MakeInlineRunner(),
                                                     library: "foo",
                                                     version: "1.0",
                                                     dryRun: false,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await chunkRepo.Received(1).DeleteChunksAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await pageRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await profileRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await indexRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await bm25Repo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await excludedRepo.Received(1).DeleteAsync("foo", "1.0", Arg.Any<CancellationToken>());
        await libraryRepo.Received(1).DeleteVersionAsync("foo", "1.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteLibraryDryRunAggregatesAcrossAllVersionsWithoutWriting()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pageRepo);
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);

        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "foo",
                                    Hint = "h",
                                    CurrentVersion = "2.0",
                                    AllVersions = new List<string> { "1.0", "2.0" }
                                });
        chunkRepo.GetChunkCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(50);
        chunkRepo.GetChunkCountAsync("foo", "2.0", Arg.Any<CancellationToken>()).Returns(100);
        pageRepo.GetPageCountAsync("foo", "1.0", Arg.Any<CancellationToken>()).Returns(10);
        pageRepo.GetPageCountAsync("foo", "2.0", Arg.Any<CancellationToken>()).Returns(20);

        var json = await MutationTools.DeleteLibrary(factory,
                                                     MakeNoopRunner(),
                                                     library: "foo",
                                                     dryRun: true,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"Versions\":", json);
        Assert.Contains("\"Chunks\": 150", json);
        Assert.Contains("\"Pages\": 30", json);
    }

    [Fact]
    public async Task DeleteLibraryApplyQueuesJobAndDeletesEachVersionThenLibraryRow()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

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
                                    AllVersions = new List<string> { "1.0", "2.0" }
                                });
        libraryRepo.DeleteAsync("foo", Arg.Any<CancellationToken>()).Returns(2);

        var json = await MutationTools.DeleteLibrary(factory,
                                                     MakeInlineRunner(),
                                                     library: "foo",
                                                     dryRun: false,
                                                     profile: null,
                                                     ct: TestContext.Current.CancellationToken);

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await chunkRepo.Received().DeleteChunksAsync("foo", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await libraryRepo.Received(1).DeleteAsync("foo", Arg.Any<CancellationToken>());
    }

    private static IBackgroundJobRunner MakeNoopRunner()
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>())
              .Returns(Guid.NewGuid().ToString());
        return runner;
    }

    private static IBackgroundJobRunner MakeInlineRunner()
    {
        var runner = Substitute.For<IBackgroundJobRunner>();
        runner.QueueAsync(Arg.Any<BackgroundJobRecord>(),
                          Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                          Arg.Any<CancellationToken>())
              .Returns(async callInfo =>
                       {
                           var record = callInfo.Arg<BackgroundJobRecord>();
                           var execute = callInfo.Arg<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>();
                           await execute(record, null, CancellationToken.None);
                           return record.Id;
                       });
        return runner;
    }
}
