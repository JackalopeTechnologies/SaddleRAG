// JobCleanupToolsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class JobCleanupToolsTests
{
    [Fact]
    public async Task CleanupAuditLogDryRunReportsCountWithoutDeleting()
    {
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

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
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

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
    public async Task DeleteScrapeJobsRefusesWithoutFilter()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        var json = await JobCleanupTools.DeleteScrapeJobs(factory,
                                                          MakeNoopRunner(),
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
    }

    [Fact]
    public async Task DeleteScrapeJobsDryRunListsCandidatesWithoutDeleting()
    {
        var jobRepo = Substitute.For<IScrapeJobRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        var matches = new[] { MakeJobRecord(JobIdAlpha, ScrapeJobStatus.Cancelled, "actipro-wpf", "25.1") };

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        jobRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<int>(),
                                          Arg.Any<CancellationToken>()
                                         )
               .Returns(matches);
        jobRepo.CountDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                           Arg.Any<string?>(),
                                           Arg.Any<string?>(),
                                           Arg.Any<CancellationToken>()
                                          )
               .Returns(returnThis: 1L);

        var json = await JobCleanupTools.DeleteScrapeJobs(factory,
                                                          MakeNoopRunner(),
                                                          jobIds: null,
                                                          status: "Cancelled",
                                                          library: "actipro-wpf",
                                                          version: "25.1",
                                                          includeAudit: true,
                                                          dryRun: true,
                                                          profile: null,
                                                          TestContext.Current.CancellationToken
                                                         );

        Assert.Contains("\"DryRun\": true", json);
        Assert.Contains("\"ScrapeJobRows\": 1", json);
        Assert.Contains(JobIdAlpha, json);
        await jobRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await jobRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                      Arg.Any<string?>(),
                                                      Arg.Any<string?>(),
                                                      Arg.Any<CancellationToken>()
                                                     );
    }

    [Fact]
    public async Task DeleteScrapeJobsApplyByIdsCallsDeletePerJobAndCascadesAudit()
    {
        var jobRepo = Substitute.For<IScrapeJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        jobRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: true);
        auditRepo.DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: 100L);

        var json = await JobCleanupTools.DeleteScrapeJobs(factory,
                                                          MakeInlineRunner(),
                                                          jobIds: new[] { JobIdAlpha, JobIdBeta },
                                                          status: null,
                                                          library: null,
                                                          version: null,
                                                          includeAudit: true,
                                                          dryRun: false,
                                                          profile: null,
                                                          TestContext.Current.CancellationToken
                                                         );

        Assert.Contains("\"JobId\":", json);
        Assert.Contains("\"Status\": \"Queued\"", json);
        await jobRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdAlpha, Arg.Any<CancellationToken>());
        await jobRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdBeta, Arg.Any<CancellationToken>());
        await auditRepo.Received(requiredNumberOfCalls: 1).DeleteByJobIdAsync(JobIdAlpha,
                                                                              Arg.Any<CancellationToken>()
                                                                             );
        await auditRepo.Received(requiredNumberOfCalls: 1).DeleteByJobIdAsync(JobIdBeta,
                                                                              Arg.Any<CancellationToken>()
                                                                             );
    }

    [Fact]
    public async Task DeleteScrapeJobsApplyByFilterCallsDeleteManyAndCascadesAudit()
    {
        var jobRepo = Substitute.For<IScrapeJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        var matches = new[]
                          {
                              MakeJobRecord(JobIdAlpha, ScrapeJobStatus.Cancelled, "actipro-wpf", "25.1"),
                              MakeJobRecord(JobIdBeta, ScrapeJobStatus.Cancelled, "actipro-wpf", "25.1")
                          };

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        jobRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<int>(),
                                          Arg.Any<CancellationToken>()
                                         )
               .Returns(matches);
        jobRepo.DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                Arg.Any<string?>(),
                                Arg.Any<string?>(),
                                Arg.Any<CancellationToken>()
                               )
               .Returns(returnThis: 2L);
        auditRepo.DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: 50L);

        var json = await JobCleanupTools.DeleteScrapeJobs(factory,
                                                          MakeInlineRunner(),
                                                          jobIds: null,
                                                          status: "Cancelled",
                                                          library: "actipro-wpf",
                                                          version: "25.1",
                                                          includeAudit: true,
                                                          dryRun: false,
                                                          profile: null,
                                                          TestContext.Current.CancellationToken
                                                         );

        Assert.Contains("\"Status\": \"Queued\"", json);
        await auditRepo.Received(requiredNumberOfCalls: 1).DeleteByJobIdAsync(JobIdAlpha,
                                                                              Arg.Any<CancellationToken>()
                                                                             );
        await auditRepo.Received(requiredNumberOfCalls: 1).DeleteByJobIdAsync(JobIdBeta,
                                                                              Arg.Any<CancellationToken>()
                                                                             );
        await jobRepo.Received(requiredNumberOfCalls: 1).DeleteManyAsync(ScrapeJobStatus.Cancelled,
                                                                         "actipro-wpf",
                                                                         "25.1",
                                                                         Arg.Any<CancellationToken>()
                                                                        );
    }

    [Fact]
    public async Task DeleteScrapeJobsWithIncludeAuditFalseSkipsAuditDelete()
    {
        var jobRepo = Substitute.For<IScrapeJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(jobRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        jobRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: true);

        await JobCleanupTools.DeleteScrapeJobs(factory,
                                               MakeInlineRunner(),
                                               jobIds: new[] { JobIdAlpha },
                                               status: null,
                                               library: null,
                                               version: null,
                                               includeAudit: false,
                                               dryRun: false,
                                               profile: null,
                                               TestContext.Current.CancellationToken
                                              );

        await jobRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdAlpha, Arg.Any<CancellationToken>());
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteScrapeJobsRejectsUnknownStatus()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        await Assert.ThrowsAsync<ArgumentException>(() => JobCleanupTools.DeleteScrapeJobs(factory,
                                                             MakeNoopRunner(),
                                                             jobIds: null,
                                                             status: "BananaPhone",
                                                             library: null,
                                                             version: null,
                                                             includeAudit: true,
                                                             dryRun: true,
                                                             profile: null,
                                                             TestContext.Current.CancellationToken
                                                        )
                                                    );
    }

    private static ScrapeJobRecord MakeJobRecord(string id, ScrapeJobStatus status, string libraryId, string version) =>
        new ScrapeJobRecord
            {
                Id = id,
                Status = status,
                CreatedAt = DateTime.UtcNow,
                Job = new ScrapeJob
                          {
                              LibraryId = libraryId,
                              Version = version,
                              RootUrl = ExampleRootUrl,
                              LibraryHint = libraryId,
                              AllowedUrlPatterns = new[] { ExampleRootUrl }
                          }
            };

    private const string ExampleRootUrl = "https://example.com";

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
}
