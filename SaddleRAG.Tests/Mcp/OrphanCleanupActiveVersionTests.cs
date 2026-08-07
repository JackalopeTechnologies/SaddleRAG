// OrphanCleanupActiveVersionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Mcp;

public sealed class OrphanCleanupActiveVersionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildingVersionIsProtectedWhenAbsentFromLibraryAllVersions(bool dryRun)
    {
        var fixture = BuildFixture(new LibraryVersionKey("lib", "v2"));
        fixture.Libraries.GetVersionsByPublicationStateAsync(VersionPublicationState.Building,
                                                             Arg.Any<CancellationToken>())
               .Returns(new[] { Version("lib", "v2", VersionPublicationState.Building) });

        var json = await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                            dryRun ? NoopRunner() : InlineRunner(),
                                                            dryRun: dryRun,
                                                            ct: TestContext.Current.CancellationToken);

        if (dryRun)
            Assert.Contains("\"OrphanedPairs\": 0", json);
        await fixture.Pages.DidNotReceive()
                     .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunningScrapeJobProtectsCandidateBeforeBuildingRecordIsVisible(bool dryRun)
    {
        var fixture = BuildFixture(new LibraryVersionKey("lib", "v2"));
        fixture.Jobs.ListRunningAsync(JobType.Scrape, Arg.Any<CancellationToken>())
               .Returns(new[]
                            {
                                new JobRecord
                                    {
                                        Id = "job", JobType = JobType.Scrape, LibraryId = "lib", Version = "v2",
                                        Status = JobStatus.Running
                                    }
                            });

        var json = await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                            dryRun ? NoopRunner() : InlineRunner(),
                                                            dryRun: dryRun,
                                                            ct: TestContext.Current.CancellationToken);

        if (dryRun)
            Assert.Contains("\"OrphanedPairs\": 0", json);
        await fixture.Pages.DidNotReceive()
                     .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TerminalJobDoesNotProtectRealOrphan()
    {
        var fixture = BuildFixture(new LibraryVersionKey("lib", "v2"));
        fixture.Jobs.ListRunningAsync(JobType.Scrape, Arg.Any<CancellationToken>())
               .Returns(Array.Empty<JobRecord>());
        fixture.Pages.DeleteAsync("lib", "v2", Arg.Any<CancellationToken>()).Returns(1L);

        await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                InlineRunner(),
                                                dryRun: false,
                                                ct: TestContext.Current.CancellationToken);

        await fixture.Pages.Received(1).DeleteAsync("lib", "v2", Arg.Any<CancellationToken>());
    }

    private static Fixture BuildFixture(LibraryVersionKey pagePair)
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var libraries = Substitute.For<ILibraryRepository>();
        var jobs = Substitute.For<IJobRepository>();
        var pages = Substitute.For<IPageRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        var profiles = Substitute.For<ILibraryProfileRepository>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        var bm25 = Substitute.For<IBm25ShardRepository>();
        var excluded = Substitute.For<IExcludedSymbolsRepository>();
        var audit = Substitute.For<IScrapeAuditRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraries);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobs);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pages);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunks);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profiles);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexes);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25);
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excluded);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(audit);
        libraries.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryRecord>());
        libraries.GetVersionsByPublicationStateAsync(VersionPublicationState.Building,
                                                     Arg.Any<CancellationToken>())
                 .Returns(Array.Empty<LibraryVersionRecord>());
        jobs.ListRunningAsync(JobType.Scrape, Arg.Any<CancellationToken>()).Returns(Array.Empty<JobRecord>());
        pages.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>()).Returns(new[] { pagePair });
        chunks.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryVersionKey>());
        profiles.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryVersionKey>());
        indexes.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryVersionKey>());
        bm25.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryVersionKey>());
        excluded.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryVersionKey>());
        audit.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryVersionKey>());
        return new Fixture(factory, libraries, jobs, pages);
    }

    private static LibraryVersionRecord Version(string library, string version, VersionPublicationState state) => new()
        {
            Id = $"{library}/{version}", LibraryId = library, Version = version, ScrapedAt = DateTime.UtcNow,
            PageCount = 0, ChunkCount = 0, EmbeddingProviderId = "p", EmbeddingModelName = "m",
            EmbeddingDimensions = 2, PublicationState = state
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

    private sealed record Fixture(RepositoryFactory Factory,
                                  ILibraryRepository Libraries,
                                  IJobRepository Jobs,
                                  IPageRepository Pages);
}
