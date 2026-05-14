// MonitorDataServiceEnrichmentTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MonitorDataServiceEnrichmentTests
{
    [Fact]
    public async Task GetLibrarySummariesAsyncSortsAlphabeticallyCaseInsensitive()
    {
        var repo = new FakeLibraryRepository();
        repo.AddLibrary(new LibraryRecord
                            {
                                Id = "zeta",
                                Name = "zeta",
                                Hint = string.Empty,
                                CurrentVersion = VersionOne,
                                AllVersions = [VersionOne]
                            }
                       );
        repo.AddLibrary(new LibraryRecord
                            {
                                Id = "Alpha",
                                Name = "Alpha",
                                Hint = string.Empty,
                                CurrentVersion = VersionOne,
                                AllVersions = [VersionOne]
                            }
                       );
        repo.AddLibrary(new LibraryRecord
                            {
                                Id = "mongodb.driver",
                                Name = "mongodb.driver",
                                Hint = string.Empty,
                                CurrentVersion = VersionOne,
                                AllVersions = [VersionOne]
                            }
                       );

        var svc = new MonitorDataService(repo,
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );
        var summaries = await svc.GetLibrarySummariesAsync(TestContext.Current.CancellationToken);

        var ids = summaries.Select(s => s.LibraryId).ToList();
        Assert.Equal(new[] { "Alpha", "mongodb.driver", "zeta" }, ids);
    }

    [Fact]
    public async Task GetLibraryDetailAsyncCarriesSuspectReasonsAndScrapedAt()
    {
        var scraped = new DateTime(year: 2026,
                                   month: 4,
                                   day: 28,
                                   hour: 12,
                                   minute: 0,
                                   second: 0,
                                   DateTimeKind.Utc
                                  );
        var libRepo = new FakeLibraryRepository();
        libRepo.AddLibrary(new LibraryRecord
                               {
                                   Id = LibraryAlpha,
                                   Name = LibraryAlpha,
                                   Hint = "hello",
                                   CurrentVersion = VersionOne,
                                   AllVersions = [VersionOne]
                               }
                          );
        libRepo.AddVersion(new LibraryVersionRecord
                               {
                                   Id = "alpha/1",
                                   LibraryId = LibraryAlpha,
                                   Version = VersionOne,
                                   ScrapedAt = scraped,
                                   PageCount = 10,
                                   ChunkCount = 100,
                                   EmbeddingProviderId = "ollama",
                                   EmbeddingModelName = "nomic-embed-text",
                                   EmbeddingDimensions = 768,
                                   BoundaryIssuePct = 2.5,
                                   Suspect = true,
                                   SuspectReasons = ["low confidence", "thin docs"]
                               }
                          );

        var svc = new MonitorDataService(libRepo,
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );
        var detail = await svc.GetLibraryDetailAsync(LibraryAlpha, TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.True(detail.IsSuspect);
        Assert.Equal(["low confidence", "thin docs"], detail.SuspectReasons);
        Assert.Equal(scraped, detail.LastScrapedAt);
        Assert.Equal(expected: 2.5, detail.BoundaryIssuePct);
        Assert.Equal("nomic-embed-text", detail.EmbeddingModelName);
    }

    [Fact]
    public async Task GetLibraryDetailAsyncReturnsHostnameDistributionAndLanguageMix()
    {
        var libRepo = new FakeLibraryRepository();
        libRepo.AddLibrary(new LibraryRecord
                               {
                                   Id = LibraryAlpha,
                                   Name = LibraryAlpha,
                                   Hint = string.Empty,
                                   CurrentVersion = VersionOne,
                                   AllVersions = [VersionOne]
                               }
                          );
        libRepo.AddVersion(new LibraryVersionRecord
                               {
                                   Id = "alpha/1",
                                   LibraryId = LibraryAlpha,
                                   Version = VersionOne,
                                   ScrapedAt = DateTime.UtcNow,
                                   PageCount = 6,
                                   ChunkCount = 60,
                                   EmbeddingProviderId = "ollama",
                                   EmbeddingModelName = "nomic-embed-text",
                                   EmbeddingDimensions = 768
                               }
                          );

        var chunkRepo = new FakeChunkRepository();
        chunkRepo.SetHosts(LibraryAlpha,
                           VersionOne,
                           new Dictionary<string, int>
                               {
                                   ["docs.aerotech.com"] = 20,
                                   ["help.aerotech.com"] = 30,
                                   ["learn.aerotech.com"] = 10
                               }
                          );
        chunkRepo.SetLanguages(LibraryAlpha,
                               VersionOne,
                               new Dictionary<string, double>
                                   {
                                       ["csharp"] = 0.6,
                                       ["unfenced"] = 0.4
                                   }
                              );

        var svc = new MonitorDataService(libRepo,
                                         chunkRepo,
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );
        var detail = await svc.GetLibraryDetailAsync(LibraryAlpha, TestContext.Current.CancellationToken);

        Assert.NotNull(detail);
        Assert.Equal(expected: 3, detail.HostnameDistribution.Count);
        Assert.Equal("help.aerotech.com", detail.HostnameDistribution[index: 0].Host);
        Assert.Equal(expected: 30, detail.HostnameDistribution[index: 0].Count);
        Assert.Equal("docs.aerotech.com", detail.HostnameDistribution[index: 1].Host);
        Assert.Equal(expected: 20, detail.HostnameDistribution[index: 1].Count);
        Assert.Equal("learn.aerotech.com", detail.HostnameDistribution[index: 2].Host);
        Assert.Equal(expected: 10, detail.HostnameDistribution[index: 2].Count);

        Assert.Equal(expected: 2, detail.LanguageMix.Count);
        Assert.Equal(expected: 0.6, detail.LanguageMix["csharp"], precision: 3);
        Assert.Equal(expected: 0.4, detail.LanguageMix["unfenced"], precision: 3);
    }

    [Fact]
    public async Task GetLibraryProfileAsyncReturnsProfileWhenPresent()
    {
        var libRepo = new FakeLibraryRepository();
        var chunkRepo = new FakeChunkRepository();
        var profileRepo = new FakeLibraryProfileRepository();

        var profile = new LibraryProfile
                          {
                              Id = "alpha/1",
                              LibraryId = LibraryAlpha,
                              Version = VersionOne,
                              Languages = ["C#"],
                              Separators = [".", "::"],
                              CallableShapes = ["Foo()", "Foo<T>()"],
                              LikelySymbols = ["Console", "WriteLine"],
                              Confidence = 0.85f,
                              Source = "calling-llm",
                              CreatedUtc = DateTime.UtcNow,
                              Casing = new CasingConventions
                                           {
                                               Types = "PascalCase",
                                               Methods = "PascalCase",
                                               Constants = "SCREAMING_SNAKE",
                                               Members = "PascalCase",
                                               Parameters = "camelCase"
                                           }
                          };
        profileRepo.SetProfile(profile);

        var svc = new MonitorDataService(libRepo,
                                         chunkRepo,
                                         profileRepo,
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );

        var loaded = await svc.GetLibraryProfileAsync(LibraryAlpha, VersionOne, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(LibraryAlpha, loaded.LibraryId);
        Assert.Equal(["C#"], loaded.Languages);
        Assert.Equal(["Console", "WriteLine"], loaded.LikelySymbols);
        Assert.Equal("PascalCase", loaded.Casing.Types);
        Assert.Equal("camelCase", loaded.Casing.Parameters);
        Assert.Equal(expected: 0.85f, loaded.Confidence);
    }

    [Fact]
    public async Task GetLibraryProfileAsyncReturnsNullWhenAbsent()
    {
        var svc = new MonitorDataService(new FakeLibraryRepository(),
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );
        var loaded = await svc.GetLibraryProfileAsync("missing", VersionOne, TestContext.Current.CancellationToken);
        Assert.Null(loaded);
    }

    [Fact]
    public async Task GetVersionsAsyncReturnsVersionsSortedDescendingByScrapedAt()
    {
        var libRepo = new FakeLibraryRepository();
        libRepo.AddLibrary(new LibraryRecord
                               {
                                   Id = LibraryAlpha,
                                   CurrentVersion = VersionTwo,
                                   Hint = string.Empty,
                                   Name = LibraryAlpha,
                                   AllVersions = [VersionOne, VersionTwo]
                               }
                          );
        var older = new DateTime(year: 2026,
                                 month: 1,
                                 day: 1,
                                 hour: 12,
                                 minute: 0,
                                 second: 0,
                                 DateTimeKind.Utc
                                );
        var newer = new DateTime(year: 2026,
                                 month: 4,
                                 day: 1,
                                 hour: 12,
                                 minute: 0,
                                 second: 0,
                                 DateTimeKind.Utc
                                );
        libRepo.AddVersion(new LibraryVersionRecord
                               {
                                   Id = "alpha/1",
                                   LibraryId = LibraryAlpha,
                                   Version = VersionOne,
                                   ScrapedAt = older,
                                   PageCount = 5,
                                   ChunkCount = 50,
                                   EmbeddingProviderId = "ollama",
                                   EmbeddingModelName = "nomic-embed-text",
                                   EmbeddingDimensions = 768
                               }
                          );
        libRepo.AddVersion(new LibraryVersionRecord
                               {
                                   Id = "alpha/2",
                                   LibraryId = LibraryAlpha,
                                   Version = VersionTwo,
                                   ScrapedAt = newer,
                                   PageCount = 7,
                                   ChunkCount = 70,
                                   EmbeddingProviderId = "ollama",
                                   EmbeddingModelName = "nomic-embed-text",
                                   EmbeddingDimensions = 768
                               }
                          );

        var svc = new MonitorDataService(libRepo,
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );
        var versions = await svc.GetVersionsAsync(LibraryAlpha, TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, versions.Count);
        Assert.Equal(VersionTwo, versions[index: 0].Version);
        Assert.Equal(VersionOne, versions[index: 1].Version);
    }

    [Fact]
    public async Task GetVersionsAsyncReturnsEmptyWhenLibraryHasNoVersions()
    {
        var svc = new MonitorDataService(new FakeLibraryRepository(),
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );
        var versions = await svc.GetVersionsAsync("missing", TestContext.Current.CancellationToken);
        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetLatestJobIdAsyncReturnsMostRecentMatchingJob()
    {
        var jobRepo = new FakeJobRepository();
        jobRepo.Add(MakeScrapeJob("old-job",
                                  LibraryAlpha,
                                  VersionOne,
                                  new DateTime(year: 2026,
                                               month: 1,
                                               day: 1,
                                               hour: 12,
                                               minute: 0,
                                               second: 0,
                                               DateTimeKind.Utc
                                              )
                                 )
                   );
        jobRepo.Add(MakeScrapeJob("new-job",
                                  LibraryAlpha,
                                  VersionOne,
                                  new DateTime(year: 2026,
                                               month: 4,
                                               day: 1,
                                               hour: 12,
                                               minute: 0,
                                               second: 0,
                                               DateTimeKind.Utc
                                              )
                                 )
                   );
        jobRepo.Add(MakeScrapeJob("other-library",
                                  "beta",
                                  VersionOne,
                                  new DateTime(year: 2026,
                                               month: 5,
                                               day: 1,
                                               hour: 12,
                                               minute: 0,
                                               second: 0,
                                               DateTimeKind.Utc
                                              )
                                 )
                   );

        var svc = new MonitorDataService(new FakeLibraryRepository(),
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         new UnifiedJobView(jobRepo),
                                         new FakeScrapeAuditRepository()
                                        );
        var jobId = await svc.GetLatestJobIdAsync(LibraryAlpha, VersionOne, TestContext.Current.CancellationToken);

        Assert.Equal("new-job", jobId);
    }

    [Fact]
    public async Task GetLatestJobIdAsyncReturnsNullWhenNoMatch()
    {
        var svc = new MonitorDataService(new FakeLibraryRepository(),
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );
        var jobId = await svc.GetLatestJobIdAsync("missing", VersionOne, TestContext.Current.CancellationToken);
        Assert.Null(jobId);
    }

    [Fact]
    public async Task GetAuditSummaryAsyncReturnsSummaryWhenPresent()
    {
        var auditRepo = new FakeScrapeAuditRepository();
        auditRepo.SetSummary("job-1",
                             new AuditSummary
                                 {
                                     JobId = "job-1",
                                     TotalConsidered = 100,
                                     IndexedCount = 50,
                                     FetchedCount = 60,
                                     FailedCount = 5,
                                     SkippedCount = 35,
                                     SkipReasonCounts = new Dictionary<AuditSkipReason, int>
                                                            {
                                                                [AuditSkipReason.PatternExclude] = 20,
                                                                [AuditSkipReason.AlreadyVisited] = 15
                                                            },
                                     HostCounts = new Dictionary<string, int>
                                                      {
                                                          ["docs.x.com"] = 80,
                                                          ["help.x.com"] = 20
                                                      }
                                 }
                            );

        var svc = new MonitorDataService(new FakeLibraryRepository(),
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         auditRepo
                                        );
        var summary = await svc.GetAuditSummaryAsync("job-1", TestContext.Current.CancellationToken);

        Assert.NotNull(summary);
        Assert.Equal(expected: 100, summary.TotalConsidered);
        Assert.Equal(expected: 50, summary.IndexedCount);
        Assert.Equal(expected: 2, summary.SkipReasonCounts.Count);
    }

    [Fact]
    public async Task GetAuditSummaryAsyncReturnsNullWhenAuditMissing()
    {
        var svc = new MonitorDataService(new FakeLibraryRepository(),
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );
        var summary = await svc.GetAuditSummaryAsync("missing-job", TestContext.Current.CancellationToken);
        Assert.Null(summary);
    }

    [Fact]
    public async Task GetJobInfoAsyncReturnsInfoWhenJobExists()
    {
        var jobRepo = new FakeJobRepository();
        var started = new DateTime(year: 2026,
                                   month: 4,
                                   day: 1,
                                   hour: 12,
                                   minute: 0,
                                   second: 0,
                                   DateTimeKind.Utc
                                  );
        jobRepo.Add(new JobRecord
                        {
                            Id = "job-1",
                            JobType = JobType.Scrape,
                            LibraryId = LibraryAlpha,
                            Version = VersionOne,
                            InputJson = "{}",
                            Status = JobStatus.Running,
                            StartedAt = started,
                            CreatedAt = new DateTime(year: 2026,
                                                     month: 4,
                                                     day: 1,
                                                     hour: 11,
                                                     minute: 59,
                                                     second: 0,
                                                     DateTimeKind.Utc
                                                    )
                        }
                   );

        var svc = new MonitorDataService(new FakeLibraryRepository(),
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         new UnifiedJobView(jobRepo),
                                         new FakeScrapeAuditRepository()
                                        );
        var info = await svc.GetJobInfoAsync("job-1", TestContext.Current.CancellationToken);

        Assert.NotNull(info);
        Assert.Equal("job-1", info.JobId);
        Assert.Equal(LibraryAlpha, info.LibraryId);
        Assert.Equal(VersionOne, info.Version);
        Assert.Equal("Running", info.Status);
        Assert.Equal(started, info.StartedAt);
    }

    [Fact]
    public async Task GetJobInfoAsyncReturnsNullWhenJobMissing()
    {
        var svc = new MonitorDataService(new FakeLibraryRepository(),
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         new FakeScrapeAuditRepository()
                                        );
        var info = await svc.GetJobInfoAsync("missing", TestContext.Current.CancellationToken);
        Assert.Null(info);
    }

    [Fact]
    public async Task GetTerminalFeedsAsyncProjectsAuditEntriesIntoFeeds()
    {
        var auditRepo = new FakeScrapeAuditRepository();
        auditRepo.AddEntry(new ScrapeAuditLogEntry
                               {
                                   Id = "e1",
                                   JobId = "job-1",
                                   LibraryId = LibraryAlpha,
                                   Version = VersionOne,
                                   Url = "https://docs.x/page-a",
                                   Host = "docs.x",
                                   Depth = 1,
                                   DiscoveredAt = new DateTime(year: 2026,
                                                               month: 4,
                                                               day: 1,
                                                               hour: 12,
                                                               minute: 0,
                                                               second: 0,
                                                               DateTimeKind.Utc
                                                              ),
                                   Status = AuditStatus.Fetched
                               }
                          );
        auditRepo.AddEntry(new ScrapeAuditLogEntry
                               {
                                   Id = "e2",
                                   JobId = "job-1",
                                   LibraryId = LibraryAlpha,
                                   Version = VersionOne,
                                   Url = "https://docs.x/page-b",
                                   Host = "docs.x",
                                   Depth = 1,
                                   DiscoveredAt = new DateTime(year: 2026,
                                                               month: 4,
                                                               day: 1,
                                                               hour: 12,
                                                               minute: 0,
                                                               second: 1,
                                                               DateTimeKind.Utc
                                                              ),
                                   Status = AuditStatus.Skipped,
                                   SkipReason = AuditSkipReason.PatternExclude
                               }
                          );

        var svc = new MonitorDataService(new FakeLibraryRepository(),
                                         new FakeChunkRepository(),
                                         new FakeLibraryProfileRepository(),
                                         EmptyJobView(),
                                         auditRepo
                                        );
        (var fetches, var rejects) = await svc.GetTerminalFeedsAsync("job-1",
                                                                     limit: 50,
                                                                     TestContext.Current.CancellationToken
                                                                    );

        Assert.Single(fetches);
        Assert.Equal("https://docs.x/page-a", fetches[index: 0].Url);
        Assert.Equal(new DateTime(year: 2026,
                                  month: 4,
                                  day: 1,
                                  hour: 12,
                                  minute: 0,
                                  second: 0,
                                  DateTimeKind.Utc
                                 ),
                     fetches[index: 0].At
                    );
        Assert.Single(rejects);
        Assert.Equal("https://docs.x/page-b", rejects[index: 0].Url);
        Assert.Equal("PatternExclude", rejects[index: 0].Reason);
    }

    #region Helper methods

    private static UnifiedJobView EmptyJobView() => new UnifiedJobView(new FakeJobRepository());

    private static JobRecord MakeScrapeJob(string id, string libraryId, string version, DateTime createdAt) =>
        new JobRecord
            {
                Id = id,
                JobType = JobType.Scrape,
                LibraryId = libraryId,
                Version = version,
                InputJson = "{}",
                Status = JobStatus.Completed,
                CreatedAt = createdAt
            };

    private const string LibraryAlpha = "alpha";
    private const string VersionOne   = "1";
    private const string VersionTwo   = "2";

    #endregion
}
