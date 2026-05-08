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
    public async Task CleanupJobsRefusesWithoutFilter()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        var json = await JobCleanupTools.CleanupJobs(factory,
                                                     MakeNoopRunner(),
                                                     kind: null,
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
    public async Task CleanupJobsDryRunCoversAllThreeCollectionsByDefault()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);

        scrapeRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                             Arg.Any<string?>(),
                                             Arg.Any<string?>(),
                                             Arg.Any<int>(),
                                             Arg.Any<CancellationToken>()
                                            )
                  .Returns(new[] { MakeScrapeJob(JobIdAlpha, ScrapeJobStatus.Cancelled, "actipro-wpf", "25.1") });
        scrapeRepo.CountDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                              Arg.Any<string?>(),
                                              Arg.Any<string?>(),
                                              Arg.Any<CancellationToken>()
                                             )
                  .Returns(returnThis: 1L);

        bgRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                         Arg.Any<string?>(),
                                         Arg.Any<string?>(),
                                         Arg.Any<int>(),
                                         Arg.Any<CancellationToken>()
                                        )
              .Returns(Array.Empty<BackgroundJobRecord>());
        bgRepo.CountDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<string?>(),
                                          Arg.Any<CancellationToken>()
                                         )
              .Returns(returnThis: 0L);

        rescrubRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                              Arg.Any<string?>(),
                                              Arg.Any<string?>(),
                                              Arg.Any<int>(),
                                              Arg.Any<CancellationToken>()
                                             )
                   .Returns(Array.Empty<RescrubJobRecord>());
        rescrubRepo.CountDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                               Arg.Any<string?>(),
                                               Arg.Any<string?>(),
                                               Arg.Any<CancellationToken>()
                                              )
                   .Returns(returnThis: 0L);

        var json = await JobCleanupTools.CleanupJobs(factory,
                                                     MakeNoopRunner(),
                                                     kind: null,
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
        Assert.Contains("\"BackgroundJobRows\": 0", json);
        Assert.Contains("\"RescrubJobRows\": 0", json);
        Assert.Contains(JobIdAlpha, json);
        await scrapeRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsKindScopeLimitsToScrapeOnly()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);

        scrapeRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                             Arg.Any<string?>(),
                                             Arg.Any<string?>(),
                                             Arg.Any<int>(),
                                             Arg.Any<CancellationToken>()
                                            )
                  .Returns(new[] { MakeScrapeJob(JobIdAlpha, ScrapeJobStatus.Cancelled, "actipro-wpf", "25.1") });
        scrapeRepo.CountDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                              Arg.Any<string?>(),
                                              Arg.Any<string?>(),
                                              Arg.Any<CancellationToken>()
                                             )
                  .Returns(returnThis: 1L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeNoopRunner(),
                                          kind: "scrape",
                                          jobIds: null,
                                          status: "Cancelled",
                                          library: "actipro-wpf",
                                          version: "25.1",
                                          includeAudit: true,
                                          dryRun: true,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await scrapeRepo.Received().CountDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                                               Arg.Any<string?>(),
                                                               Arg.Any<string?>(),
                                                               Arg.Any<CancellationToken>()
                                                              );
        await bgRepo.DidNotReceive().CountDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                                                Arg.Any<string?>(),
                                                                Arg.Any<string?>(),
                                                                Arg.Any<CancellationToken>()
                                                               );
        await rescrubRepo.DidNotReceive().CountDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                                                     Arg.Any<string?>(),
                                                                     Arg.Any<string?>(),
                                                                     Arg.Any<CancellationToken>()
                                                                    );
    }

    [Fact]
    public async Task CleanupJobsApplyByIdsCallsDeleteAcrossAllRepositoriesAndCascadesAudit()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);

        scrapeRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: true);
        bgRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: true);
        rescrubRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: true);
        auditRepo.DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: 50L);

        var json = await JobCleanupTools.CleanupJobs(factory,
                                                     MakeInlineRunner(),
                                                     kind: null,
                                                     jobIds: new[] { JobIdAlpha, JobIdBeta },
                                                     status: null,
                                                     library: null,
                                                     version: null,
                                                     includeAudit: true,
                                                     dryRun: false,
                                                     profile: null,
                                                     TestContext.Current.CancellationToken
                                                    );

        Assert.Contains("\"Status\": \"Queued\"", json);
        await scrapeRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdAlpha, Arg.Any<CancellationToken>());
        await scrapeRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdBeta, Arg.Any<CancellationToken>());
        await bgRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdAlpha, Arg.Any<CancellationToken>());
        await bgRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdBeta, Arg.Any<CancellationToken>());
        await rescrubRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdAlpha, Arg.Any<CancellationToken>());
        await rescrubRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdBeta, Arg.Any<CancellationToken>());
        await auditRepo.Received(requiredNumberOfCalls: 1).DeleteByJobIdAsync(JobIdAlpha,
                                                                              Arg.Any<CancellationToken>()
                                                                             );
        await auditRepo.Received(requiredNumberOfCalls: 1).DeleteByJobIdAsync(JobIdBeta,
                                                                              Arg.Any<CancellationToken>()
                                                                             );
    }

    [Fact]
    public async Task CleanupJobsApplyByFilterCallsDeleteManyOnEachRepoAndCascadesAuditForScrape()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        var matches = new[]
                          {
                              MakeScrapeJob(JobIdAlpha, ScrapeJobStatus.Cancelled, "actipro-wpf", "25.1"),
                              MakeScrapeJob(JobIdBeta, ScrapeJobStatus.Cancelled, "actipro-wpf", "25.1")
                          };

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);

        scrapeRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                             Arg.Any<string?>(),
                                             Arg.Any<string?>(),
                                             Arg.Any<int>(),
                                             Arg.Any<CancellationToken>()
                                            )
                  .Returns(matches);
        scrapeRepo.DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                   Arg.Any<string?>(),
                                   Arg.Any<string?>(),
                                   Arg.Any<CancellationToken>()
                                  )
                  .Returns(returnThis: 2L);
        bgRepo.DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                               Arg.Any<string?>(),
                               Arg.Any<string?>(),
                               Arg.Any<CancellationToken>()
                              )
              .Returns(returnThis: 0L);
        rescrubRepo.DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                    Arg.Any<string?>(),
                                    Arg.Any<string?>(),
                                    Arg.Any<CancellationToken>()
                                   )
                   .Returns(returnThis: 0L);
        auditRepo.DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: 50L);

        var json = await JobCleanupTools.CleanupJobs(factory,
                                                     MakeInlineRunner(),
                                                     kind: null,
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
        await scrapeRepo.Received(requiredNumberOfCalls: 1).DeleteManyAsync(ScrapeJobStatus.Cancelled,
                                                                            "actipro-wpf",
                                                                            "25.1",
                                                                            Arg.Any<CancellationToken>()
                                                                           );
        await bgRepo.Received(requiredNumberOfCalls: 1).DeleteManyAsync(ScrapeJobStatus.Cancelled,
                                                                        "actipro-wpf",
                                                                        "25.1",
                                                                        Arg.Any<CancellationToken>()
                                                                       );
        await rescrubRepo.Received(requiredNumberOfCalls: 1).DeleteManyAsync(ScrapeJobStatus.Cancelled,
                                                                             "actipro-wpf",
                                                                             "25.1",
                                                                             Arg.Any<CancellationToken>()
                                                                            );
    }

    [Fact]
    public async Task CleanupJobsWithIncludeAuditFalseSkipsAuditDelete()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        scrapeRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: true);
        bgRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: false);
        rescrubRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(returnThis: false);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeInlineRunner(),
                                          kind: null,
                                          jobIds: new[] { JobIdAlpha },
                                          status: null,
                                          library: null,
                                          version: null,
                                          includeAudit: false,
                                          dryRun: false,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await scrapeRepo.Received(requiredNumberOfCalls: 1).DeleteAsync(JobIdAlpha, Arg.Any<CancellationToken>());
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsRejectsUnknownStatus()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        await Assert.ThrowsAsync<ArgumentException>(() => JobCleanupTools.CleanupJobs(factory,
                                                             MakeNoopRunner(),
                                                             kind: null,
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

    [Fact]
    public async Task CleanupJobsRejectsUnknownKind()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        await Assert.ThrowsAsync<ArgumentException>(() => JobCleanupTools.CleanupJobs(factory,
                                                             MakeNoopRunner(),
                                                             kind: "ScreenSaver",
                                                             jobIds: null,
                                                             status: null,
                                                             library: "x",
                                                             version: null,
                                                             includeAudit: true,
                                                             dryRun: true,
                                                             profile: null,
                                                             TestContext.Current.CancellationToken
                                                        )
                                                    );
    }

    [Fact]
    public async Task CleanupJobsDryRunNeverInvokesAnyDeleteMethod()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);

        scrapeRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                             Arg.Any<string?>(),
                                             Arg.Any<string?>(),
                                             Arg.Any<int>(),
                                             Arg.Any<CancellationToken>()
                                            )
                  .Returns(Array.Empty<ScrapeJobRecord>());
        bgRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                         Arg.Any<string?>(),
                                         Arg.Any<string?>(),
                                         Arg.Any<int>(),
                                         Arg.Any<CancellationToken>()
                                        )
              .Returns(Array.Empty<BackgroundJobRecord>());
        rescrubRepo.ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                              Arg.Any<string?>(),
                                              Arg.Any<string?>(),
                                              Arg.Any<int>(),
                                              Arg.Any<CancellationToken>()
                                             )
                   .Returns(Array.Empty<RescrubJobRecord>());

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeNoopRunner(),
                                          kind: null,
                                          jobIds: new[] { JobIdAlpha, JobIdBeta },
                                          status: "Failed",
                                          library: "any",
                                          version: "1.0",
                                          includeAudit: true,
                                          dryRun: true,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await scrapeRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await scrapeRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                         Arg.Any<string?>(),
                                                         Arg.Any<string?>(),
                                                         Arg.Any<CancellationToken>()
                                                        );
        await bgRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await bgRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                     Arg.Any<string?>(),
                                                     Arg.Any<string?>(),
                                                     Arg.Any<CancellationToken>()
                                                    );
        await rescrubRepo.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await rescrubRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                          Arg.Any<string?>(),
                                                          Arg.Any<string?>(),
                                                          Arg.Any<CancellationToken>()
                                                         );
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await auditRepo.DidNotReceive().DeleteByLibraryVersionAsync(Arg.Any<string>(),
                                                                    Arg.Any<string>(),
                                                                    Arg.Any<CancellationToken>()
                                                                   );
    }

    [Fact]
    public async Task CleanupJobsKindBackgroundOnlyTouchesBackgroundRepoAndSkipsAuditCascade()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        bgRepo.DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                               Arg.Any<string?>(),
                               Arg.Any<string?>(),
                               Arg.Any<CancellationToken>()
                              )
              .Returns(returnThis: 3L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeInlineRunner(),
                                          kind: "background",
                                          jobIds: null,
                                          status: "Failed",
                                          library: null,
                                          version: null,
                                          includeAudit: true,
                                          dryRun: false,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await bgRepo.Received(requiredNumberOfCalls: 1).DeleteManyAsync(ScrapeJobStatus.Failed,
                                                                        null,
                                                                        null,
                                                                        Arg.Any<CancellationToken>()
                                                                       );
        await scrapeRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                         Arg.Any<string?>(),
                                                         Arg.Any<string?>(),
                                                         Arg.Any<CancellationToken>()
                                                        );
        await rescrubRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                          Arg.Any<string?>(),
                                                          Arg.Any<string?>(),
                                                          Arg.Any<CancellationToken>()
                                                         );
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsKindRescrubOnlyTouchesRescrubRepo()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        rescrubRepo.DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                    Arg.Any<string?>(),
                                    Arg.Any<string?>(),
                                    Arg.Any<CancellationToken>()
                                   )
                   .Returns(returnThis: 1L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeInlineRunner(),
                                          kind: "rescrub",
                                          jobIds: null,
                                          status: null,
                                          library: "lib-x",
                                          version: null,
                                          includeAudit: true,
                                          dryRun: false,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await rescrubRepo.Received(requiredNumberOfCalls: 1).DeleteManyAsync(null,
                                                                             "lib-x",
                                                                             null,
                                                                             Arg.Any<CancellationToken>()
                                                                            );
        await scrapeRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                         Arg.Any<string?>(),
                                                         Arg.Any<string?>(),
                                                         Arg.Any<CancellationToken>()
                                                        );
        await bgRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                     Arg.Any<string?>(),
                                                     Arg.Any<string?>(),
                                                     Arg.Any<CancellationToken>()
                                                    );
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsApplyByFilterRespectsIncludeAuditFalse()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var auditRepo = Substitute.For<IScrapeAuditRepository>();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);
        factory.GetScrapeAuditRepository(Arg.Any<string?>()).Returns(auditRepo);
        scrapeRepo.DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                   Arg.Any<string?>(),
                                   Arg.Any<string?>(),
                                   Arg.Any<CancellationToken>()
                                  )
                  .Returns(returnThis: 5L);
        bgRepo.DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                               Arg.Any<string?>(),
                               Arg.Any<string?>(),
                               Arg.Any<CancellationToken>()
                              )
              .Returns(returnThis: 0L);
        rescrubRepo.DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                    Arg.Any<string?>(),
                                    Arg.Any<string?>(),
                                    Arg.Any<CancellationToken>()
                                   )
                   .Returns(returnThis: 0L);

        await JobCleanupTools.CleanupJobs(factory,
                                          MakeInlineRunner(),
                                          kind: null,
                                          jobIds: null,
                                          status: "Failed",
                                          library: null,
                                          version: null,
                                          includeAudit: false,
                                          dryRun: false,
                                          profile: null,
                                          TestContext.Current.CancellationToken
                                         );

        await scrapeRepo.DidNotReceive().ListDeleteCandidatesAsync(Arg.Any<ScrapeJobStatus?>(),
                                                                   Arg.Any<string?>(),
                                                                   Arg.Any<string?>(),
                                                                   Arg.Any<int>(),
                                                                   Arg.Any<CancellationToken>()
                                                                  );
        await auditRepo.DidNotReceive().DeleteByJobIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CleanupJobsRefusalReturnsZeroDeletionsAndDoesNotQueueJob()
    {
        var scrapeRepo = Substitute.For<IScrapeJobRepository>();
        var bgRepo = Substitute.For<IBackgroundJobRepository>();
        var rescrubRepo = Substitute.For<IRescrubJobRepository>();
        var runner = MakeNoopRunner();
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });

        factory.GetScrapeJobRepository(Arg.Any<string?>()).Returns(scrapeRepo);
        factory.GetBackgroundJobRepository(Arg.Any<string?>()).Returns(bgRepo);
        factory.GetRescrubJobRepository(Arg.Any<string?>()).Returns(rescrubRepo);

        var json = await JobCleanupTools.CleanupJobs(factory,
                                                     runner,
                                                     kind: "all",
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
        await scrapeRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                         Arg.Any<string?>(),
                                                         Arg.Any<string?>(),
                                                         Arg.Any<CancellationToken>()
                                                        );
        await bgRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                     Arg.Any<string?>(),
                                                     Arg.Any<string?>(),
                                                     Arg.Any<CancellationToken>()
                                                    );
        await rescrubRepo.DidNotReceive().DeleteManyAsync(Arg.Any<ScrapeJobStatus?>(),
                                                          Arg.Any<string?>(),
                                                          Arg.Any<string?>(),
                                                          Arg.Any<CancellationToken>()
                                                         );
    }

    private static ScrapeJobRecord MakeScrapeJob(string id,
                                                 ScrapeJobStatus status,
                                                 string libraryId,
                                                 string version) =>
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
    private const string ExampleRootUrl = "https://example.com";
}
