// Stage 7 RED contract for in-progress directory-scan orphan protection.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Mcp;

public sealed class DirectoryScanOrphanProtectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunningDirectoryScanProtectsCandidatePairsBeforePublicationIsVisible(bool dryRun)
    {
        Fixture fixture = BuildFixture(new LibraryVersionKey(LibraryId, Version));
        fixture.Jobs.ListRunningAsync(JobType.DirectoryScan, Arg.Any<CancellationToken>())
               .Returns([
                            new JobRecord
                                {
                                    Id = "directory-job",
                                    JobType = JobType.DirectoryScan,
                                    LibraryId = LibraryId,
                                    Version = Version,
                                    Status = JobStatus.Running
                                }
                        ]);

        string json = await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                               dryRun ? NoopRunner() : InlineRunner(),
                                                               dryRun: dryRun,
                                                               ct: TestContext.Current.CancellationToken);

        if (dryRun)
            Assert.Contains("\"OrphanedPairs\": 0", json, StringComparison.Ordinal);
        await fixture.Pages.DidNotReceive()
                     .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await fixture.Jobs.Received(requiredNumberOfCalls: 1)
                     .ListRunningAsync(JobType.DirectoryScan, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TerminalDirectoryScanDoesNotProtectARealOrphan()
    {
        Fixture fixture = BuildFixture(new LibraryVersionKey(LibraryId, Version));
        fixture.Jobs.ListRunningAsync(JobType.DirectoryScan, Arg.Any<CancellationToken>())
               .Returns(Array.Empty<JobRecord>());
        fixture.Pages.DeleteAsync(LibraryId, Version, Arg.Any<CancellationToken>()).Returns(1L);

        await OrphanCleanupTools.CleanupOrphans(fixture.Factory,
                                                InlineRunner(),
                                                dryRun: false,
                                                ct: TestContext.Current.CancellationToken);

        await fixture.Pages.Received(requiredNumberOfCalls: 1)
                     .DeleteAsync(LibraryId, Version, Arg.Any<CancellationToken>());
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
        var sources = Substitute.For<ISourceDocumentRepository>();
        var catalogs = Substitute.For<ISubjectCatalogRepository>();
        var assignments = Substitute.For<ISubjectAssignmentRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraries);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobs);
        factory.GetPageRepository(Arg.Any<string?>()).Returns(pages);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunks);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(profiles);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexes);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25);
        factory.GetExcludedSymbolsRepository(Arg.Any<string?>()).Returns(excluded);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(audit);
        factory.GetSourceDocumentRepository(Arg.Any<string?>()).Returns(sources);
        factory.GetSubjectCatalogRepository(Arg.Any<string?>()).Returns(catalogs);
        factory.GetSubjectAssignmentRepository(Arg.Any<string?>()).Returns(assignments);
        libraries.GetAllLibrariesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LibraryRecord>());
        libraries.GetVersionsByPublicationStateAsync(VersionPublicationState.Building,
                                                     Arg.Any<CancellationToken>())
                 .Returns(Array.Empty<LibraryVersionRecord>());
        jobs.ListRunningAsync(JobType.Scrape, Arg.Any<CancellationToken>()).Returns(Array.Empty<JobRecord>());
        jobs.ListRunningAsync(JobType.DirectoryScan, Arg.Any<CancellationToken>()).Returns(Array.Empty<JobRecord>());
        pages.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>()).Returns([pagePair]);
        chunks.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
              .Returns(Array.Empty<LibraryVersionKey>());
        profiles.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                .Returns(Array.Empty<LibraryVersionKey>());
        indexes.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
               .Returns(Array.Empty<LibraryVersionKey>());
        bm25.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<LibraryVersionKey>());
        excluded.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
                .Returns(Array.Empty<LibraryVersionKey>());
        audit.GetDistinctLibraryVersionPairsAsync(Arg.Any<CancellationToken>())
             .Returns(Array.Empty<LibraryVersionKey>());
        return new Fixture(factory, jobs, pages);
    }

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
                       BackgroundJobRecord record = Assert.IsType<BackgroundJobRecord>(call[index: 0]);
                       Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task> action =
                           Assert.IsType<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(
                               call[index: 1]);
                       await action(record, null, CancellationToken.None);
                       return record.Id;
                   });
        return runner;
    }

    private sealed record Fixture(RepositoryFactory Factory,
                                  IJobRepository Jobs,
                                  IPageRepository Pages);

    private const string LibraryId = "stage7-active-directory";
    private const string Version = "2026-08-04";
}
