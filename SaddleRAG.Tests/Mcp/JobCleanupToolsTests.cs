// JobCleanupToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Exercises <see cref="JobCleanupTools" /> after the four-collection
///     unification: every cleanup path now talks to a single
///     <see cref="IJobRepository" /> and audit cascade only fires for
///     <see cref="JobType.Scrape" /> rows.
/// </summary>
public sealed class JobCleanupToolsTests
{
    [Fact]
    public async Task CleanupAuditLogDryRunReportsCountWithoutDeleting()
    {
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>([null]);

        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        auditRepo.SummarizeAsync(JobIdAlpha, Arg.Any<CancellationToken>())
                 .Returns(new AuditSummary
                              {
                                  JobId = JobIdAlpha,
                                  TotalConsidered = SampleTotalConsidered,
                                  IndexedCount = 10,
                                  FetchedCount = 5,
                                  FailedCount = 1,
                                  SkippedCount = 84,
                                  SkipReasonCounts = new Dictionary<AuditSkipReason, int>(),
                                  HostCounts = new Dictionary<string, int>()
                              }
                         );

        var json = await JobCleanupTools.CleanupAuditLog(factory,
                                                         MakeNoopRunner(),
                                                         JobIdAlpha,
                                                         dryRun: true,
                                                         profile: null,
                                                         TestContext.Current.CancellationToken
                                                        );

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains($"\"AuditRows\": {SampleTotalConsidered}", json);
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupAuditLogApplyQueuesJobAndCallsDeleteByJobId()
    {
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>([null]);

        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        auditRepo.DeleteByJobIdAsync(JobIdAlpha, Arg.Any<CancellationToken>()).Returns(returnThis: 7_400_000L);

        var json = await JobCleanupTools.CleanupAuditLog(factory,
                                                         MakeInlineRunner(),
                                                         JobIdAlpha,
                                                         dryRun: false,
                                                         profile: null,
                                                         TestContext.Current.CancellationToken
                                                        );

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await auditRepo.Received(requiredNumberOfCalls: 1)
                       .DeleteByJobIdAsync(JobIdAlpha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsRefusesWithoutFilterAndDoesNotQueueJob()
    {
        var factory = Substitute.For<RepositoryFactory>([null]);
        var runner = MakeNoopRunner();

        var json = await JobCleanupTools.CleanupJobs(factory,
                                                     runner,
                                                     jobType: null,
                                                     jobIds: null,
                                                     status: null,
                                                     library: null,
                                                     version: null,
                                                     includeAudit: true,
                                                     dryRun: false,
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"Status\": \"NoFilter\"", json);
        await runner.DidNotReceive()
                    .QueueAsync(Arg.Any<BackgroundJobRecord>(),
                                Arg.Any<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>(),
                                Arg.Any<CancellationToken>()
                               );
    }

    [Fact]
    public async Task CleanupJobsDryRunWithLibraryAndStatusReportsCountFromUnifiedRepo()
    {
        var jobRepo = Substitute.For<IJobRepository>();
        var factory = MakeFactoryWithJobRepo(jobRepo);

        StubDryRun(jobRepo,
                   sample: [MakeScrapeRecord(JobIdAlpha, JobStatus.Cancelled, LibraryActipro, VersionTwentyFiveOne)],
                   total: 1L
                  );

        var json = await JobCleanupTools.CleanupJobs(factory,
                                                     MakeNoopRunner(),
                                                     jobType: null,
                                                     jobIds: null,
                                                     "Cancelled",
                                                     LibraryActipro,
                                                     VersionTwentyFiveOne,
                                                     includeAudit: true,
                                                     dryRun: true,
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"JobRows\": 1", json);
        Assert.Contains("\"AuditCascade\": true", json);
        Assert.Contains(JobIdAlpha, json);
        await jobRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobRepo.DidNotReceive().DeleteManyAsync(Arg.Any<JobType?>(),
                                                      Arg.Any<JobStatus?>(),
                                                      Arg.Any<string?>(),
                                                      Arg.Any<string?>(),
                                                      Arg.Any<DateTime?>(),
                                                      Arg.Any<CancellationToken>()
                                                     );
    }

    [Fact]
    public async Task CleanupJobsDryRunWithJobTypeScrapeForwardsFilterToRepo()
    {
        var jobRepo = Substitute.For<IJobRepository>();
        var factory = MakeFactoryWithJobRepo(jobRepo);
        StubDryRun(jobRepo, sample: [], total: 0L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeNoopRunner(),
                                          "Scrape",
                                          jobIds: null,
                                          "Cancelled",
                                          LibraryActipro,
                                          VersionTwentyFiveOne,
                                          includeAudit: true,
                                          dryRun: true,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await jobRepo.Received()
                     .CountDeleteCandidatesAsync(JobType.Scrape,
                                                 JobStatus.Cancelled,
                                                 LibraryActipro,
                                                 VersionTwentyFiveOne,
                                                 Arg.Any<DateTime?>(),
                                                 Arg.Any<CancellationToken>()
                                                );
    }

    [Fact]
    public async Task CleanupJobsApplyByIdsDeletesEachRecordAndCascadesAuditForScrapeOnly()
    {
        var jobRepo = Substitute.For<IJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = MakeFactoryWithJobRepo(jobRepo, auditRepo);

        jobRepo.GetAsync(JobIdAlpha, Arg.Any<CancellationToken>())
               .Returns(MakeScrapeRecord(JobIdAlpha, JobStatus.Completed, LibraryActipro, VersionTwentyFiveOne));
        jobRepo.GetAsync(JobIdBeta, Arg.Any<CancellationToken>())
               .Returns(MakeRechunkRecord(JobIdBeta, JobStatus.Completed, LibraryBeta, VersionTwentyFiveOne));
        jobRepo.DeleteAsync(JobIdAlpha, Arg.Any<CancellationToken>()).Returns(returnThis: true);
        jobRepo.DeleteAsync(JobIdBeta, Arg.Any<CancellationToken>()).Returns(returnThis: true);
        auditRepo.DeleteByJobIdAsync(JobIdAlpha, Arg.Any<CancellationToken>()).Returns(returnThis: 7L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeInlineRunner(),
                                          jobType: null,
                                          jobIds: [JobIdAlpha, JobIdBeta],
                                          status: null,
                                          library: null,
                                          version: null,
                                          includeAudit: true,
                                          dryRun: false,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await jobRepo.Received().DeleteAsync(JobIdAlpha, Arg.Any<CancellationToken>());
        await jobRepo.Received().DeleteAsync(JobIdBeta, Arg.Any<CancellationToken>());
        await auditRepo.Received().DeleteByJobIdAsync(JobIdAlpha, Arg.Any<CancellationToken>());
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(JobIdBeta, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsApplyByFilterCallsDeleteManyAndCascadesAuditWhenScopeIncludesScrape()
    {
        var jobRepo = Substitute.For<IJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = MakeFactoryWithJobRepo(jobRepo, auditRepo);

        jobRepo.ListDeleteCandidatesAsync(Arg.Any<JobType?>(),
                                          Arg.Any<JobStatus?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<DateTime?>(),
                                          Arg.Any<int>(),
                                          Arg.Any<CancellationToken>()
                                         )
               .Returns([MakeScrapeRecord(JobIdAlpha, JobStatus.Cancelled, LibraryActipro, VersionTwentyFiveOne)]);
        jobRepo.DeleteManyAsync(Arg.Any<JobType?>(),
                                Arg.Any<JobStatus?>(),
                                Arg.Any<string?>(),
                                Arg.Any<string?>(),
                                Arg.Any<DateTime?>(),
                                Arg.Any<CancellationToken>()
                               )
               .Returns(returnThis: 1L);
        auditRepo.DeleteByJobIdAsync(JobIdAlpha, Arg.Any<CancellationToken>()).Returns(returnThis: 3L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeInlineRunner(),
                                          jobType: null,
                                          jobIds: null,
                                          "Cancelled",
                                          LibraryActipro,
                                          VersionTwentyFiveOne,
                                          includeAudit: true,
                                          dryRun: false,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await jobRepo.Received().DeleteManyAsync(Arg.Any<JobType?>(),
                                                 JobStatus.Cancelled,
                                                 LibraryActipro,
                                                 VersionTwentyFiveOne,
                                                 Arg.Any<DateTime?>(),
                                                 Arg.Any<CancellationToken>()
                                                );
        await auditRepo.Received().DeleteByJobIdAsync(JobIdAlpha, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsWithIncludeAuditFalseSkipsAuditCascadeOnDeleteMany()
    {
        var jobRepo = Substitute.For<IJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = MakeFactoryWithJobRepo(jobRepo, auditRepo);

        jobRepo.DeleteManyAsync(Arg.Any<JobType?>(),
                                Arg.Any<JobStatus?>(),
                                Arg.Any<string?>(),
                                Arg.Any<string?>(),
                                Arg.Any<DateTime?>(),
                                Arg.Any<CancellationToken>()
                               )
               .Returns(returnThis: 1L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeInlineRunner(),
                                          jobType: null,
                                          jobIds: null,
                                          "Cancelled",
                                          LibraryActipro,
                                          VersionTwentyFiveOne,
                                          includeAudit: false,
                                          dryRun: false,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await jobRepo.Received().DeleteManyAsync(Arg.Any<JobType?>(),
                                                 JobStatus.Cancelled,
                                                 LibraryActipro,
                                                 VersionTwentyFiveOne,
                                                 Arg.Any<DateTime?>(),
                                                 Arg.Any<CancellationToken>()
                                                );
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsJobTypeRechunkSkipsAuditCascadeEvenWhenIncludeAuditTrue()
    {
        var jobRepo = Substitute.For<IJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = MakeFactoryWithJobRepo(jobRepo, auditRepo);

        jobRepo.DeleteManyAsync(Arg.Any<JobType?>(),
                                Arg.Any<JobStatus?>(),
                                Arg.Any<string?>(),
                                Arg.Any<string?>(),
                                Arg.Any<DateTime?>(),
                                Arg.Any<CancellationToken>()
                               )
               .Returns(returnThis: 1L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeInlineRunner(),
                                          "Rechunk",
                                          jobIds: null,
                                          "Completed",
                                          LibraryActipro,
                                          VersionTwentyFiveOne,
                                          includeAudit: true,
                                          dryRun: false,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsRejectsUnknownStatus()
    {
        var factory = MakeFactoryWithJobRepo(Substitute.For<IJobRepository>());

        await Assert.ThrowsAsync<ArgumentException>(() => JobCleanupTools.CleanupJobs(factory,
                                                                                       MakeNoopRunner(),
                                                                                       jobType: null,
                                                                                       jobIds: null,
                                                                                       "Bogus",
                                                                                       library: null,
                                                                                       version: null,
                                                                                       includeAudit: true,
                                                                                       dryRun: true,
                                                                                       profile: null,
                                                                                       TestContext.Current.CancellationToken
                                                                                      )
                                                    );
    }

    [Fact]
    public async Task CleanupJobsRejectsUnknownJobType()
    {
        var factory = MakeFactoryWithJobRepo(Substitute.For<IJobRepository>());

        await Assert.ThrowsAsync<ArgumentException>(() => JobCleanupTools.CleanupJobs(factory,
                                                                                       MakeNoopRunner(),
                                                                                       "Bogus",
                                                                                       jobIds: null,
                                                                                       status: null,
                                                                                       library: null,
                                                                                       version: null,
                                                                                       includeAudit: true,
                                                                                       dryRun: true,
                                                                                       profile: null,
                                                                                       TestContext.Current.CancellationToken
                                                                                      )
                                                    );
    }

    [Fact]
    public async Task CleanupJobsDryRunNeverInvokesDeleteMethods()
    {
        var jobRepo = Substitute.For<IJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = MakeFactoryWithJobRepo(jobRepo, auditRepo);
        StubDryRun(jobRepo, sample: [], total: 0L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeNoopRunner(),
                                          jobType: null,
                                          jobIds: null,
                                          "Cancelled",
                                          LibraryActipro,
                                          VersionTwentyFiveOne,
                                          includeAudit: true,
                                          dryRun: true,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await jobRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobRepo.DidNotReceive().DeleteManyAsync(Arg.Any<JobType?>(),
                                                      Arg.Any<JobStatus?>(),
                                                      Arg.Any<string?>(),
                                                      Arg.Any<string?>(),
                                                      Arg.Any<DateTime?>(),
                                                      Arg.Any<CancellationToken>()
                                                     );
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    #region Helpers

    private static RepositoryFactory MakeFactoryWithJobRepo(IJobRepository jobRepo,
                                                            IScrapeAuditRepository? auditRepo = null)
    {
        var factory = Substitute.For<RepositoryFactory>([null]);
        factory.GetJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>())
               .Returns(auditRepo ?? Substitute.For<IScrapeAuditRepository>());
        return factory;
    }

    private static void StubDryRun(IJobRepository jobRepo, IReadOnlyList<JobRecord> sample, long total)
    {
        jobRepo.ListDeleteCandidatesAsync(Arg.Any<JobType?>(),
                                          Arg.Any<JobStatus?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<DateTime?>(),
                                          Arg.Any<int>(),
                                          Arg.Any<CancellationToken>()
                                         )
               .Returns(sample);
        jobRepo.CountDeleteCandidatesAsync(Arg.Any<JobType?>(),
                                           Arg.Any<JobStatus?>(),
                                           Arg.Any<string?>(),
                                           Arg.Any<string?>(),
                                           Arg.Any<DateTime?>(),
                                           Arg.Any<CancellationToken>()
                                          )
               .Returns(total);
    }

    private static JobRecord MakeScrapeRecord(string id,
                                              JobStatus status,
                                              string libraryId,
                                              string version) =>
        new JobRecord
            {
                Id = id,
                JobType = JobType.Scrape,
                Status = status,
                LibraryId = libraryId,
                Version = version,
                InputJson = "{}",
                CreatedAt = DateTime.UtcNow
            };

    private static JobRecord MakeRechunkRecord(string id,
                                               JobStatus status,
                                               string libraryId,
                                               string version) =>
        new JobRecord
            {
                Id = id,
                JobType = JobType.Rechunk,
                Status = status,
                LibraryId = libraryId,
                Version = version,
                InputJson = "{}",
                CreatedAt = DateTime.UtcNow
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
                           var execute = callInfo
                               .Arg<Func<BackgroundJobRecord, Action<int, int>?, CancellationToken, Task>>();
                           await execute(record, arg2: null, CancellationToken.None);
                           return record.Id;
                       }
                      );
        return runner;
    }

    private const string JobIdAlpha = "job-alpha-001";
    private const string JobIdBeta = "job-beta-002";
    private const int SampleTotalConsidered = 100;
    private const string LibraryActipro = "actipro-wpf";
    private const string LibraryBeta = "betalib";
    private const string VersionTwentyFiveOne = "25.1";

    #endregion
}
