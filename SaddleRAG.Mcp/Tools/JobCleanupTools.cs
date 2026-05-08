// JobCleanupTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     Manual cleanup tools for ScrapeAuditLog and the three job tracking
///     collections (ScrapeJobs, BackgroundJobs, RescrubJobs). Audit and job
///     rows auto-purge after 30 days via Mongo TTL indexes; these tools
///     cover early eviction and explicit-id removal. Both default to
///     dryRun=true. Apply paths queue a background job and return the
///     JobId immediately.
/// </summary>
[McpServerToolType]
public static class JobCleanupTools
{
    private enum JobKind
    {
        All,
        Scrape,
        Background,
        Rescrub
    }

    [McpServerTool(Name = "cleanup_audit_log")]
    [Description("Manually purge ScrapeAuditLog rows for a single scrape job. Useful when " +
                 "a runaway crawl has left a huge audit log behind and you want to evict " +
                 "it before the 30-day TTL fires. Defaults to dryRun=true — preview the " +
                 "row count before passing dryRun=false to apply. dryRun=false returns " +
                 "{ JobId, Status: 'Queued' } immediately; poll get_job_status for the outcome. " +
                 "Does NOT delete the ScrapeJobs row itself — use cleanup_jobs for that."
                )]
    public static async Task<string> CleanupAuditLog(RepositoryFactory repositoryFactory,
                                                     [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                     IBackgroundJobRunner runner,
                                                     [Description("Scrape job id whose audit rows should be purged")]
                                                     string jobId,
                                                     [Description("If true (default), preview without writing.")]
                                                     bool dryRun = true,
                                                     [Description("Optional database profile name")]
                                                     string? profile = null,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        string result;
        if (dryRun)
        {
            var auditRepo = repositoryFactory.GetScrapeAuditRepository(profile);
            var summary = await auditRepo.SummarizeAsync(jobId, ct);
            var preview = new
                              {
                                  DryRun = true,
                                  JobId = jobId,
                                  WouldDelete = new
                                                    {
                                                        AuditRows = summary.TotalConsidered,
                                                        ByStatus = new
                                                                       {
                                                                           summary.IndexedCount,
                                                                           summary.FetchedCount,
                                                                           summary.FailedCount,
                                                                           summary.SkippedCount
                                                                       }
                                                    },
                                  ScrapeJobRowAffected = false
                              };
            result = JsonSerializer.Serialize(preview, smJsonOptions);
        }
        else
            result = await QueueCleanupAuditLogJobAsync(jobId, repositoryFactory, runner, profile, ct);

        return result;
    }

    [McpServerTool(Name = "cleanup_jobs")]
    [Description("Manually delete job tracking rows from ScrapeJobs, BackgroundJobs, and " +
                 "RescrubJobs by explicit id list, by status (Queued/Running/Completed/" +
                 "Failed/Cancelled), by library, and/or by version. Use kind to scope to " +
                 "one collection (scrape/background/rescrub) or leave at 'all'. At least " +
                 "one of jobIds/status/library/version must be supplied — calls with no " +
                 "filter return zero. Cascades to ScrapeAuditLog by default for scrape jobs " +
                 "(includeAudit=true). Defaults to dryRun=true. dryRun=false returns " +
                 "{ JobId, Status: 'Queued' } immediately; poll get_job_status for the outcome."
                )]
    public static async Task<string> CleanupJobs(RepositoryFactory repositoryFactory,
                                                 [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                 IBackgroundJobRunner runner,
                                                 [Description("Optional collection scope: scrape, background, " +
                                                              "rescrub, or all (default)."
                                                             )]
                                                 string? kind = null,
                                                 [Description("Optional explicit list of job ids. When set, " +
                                                              "status/library/version are ignored. Each id is " +
                                                              "looked up across the in-scope collections."
                                                             )]
                                                 string[]? jobIds = null,
                                                 [Description("Optional status filter: Queued, Running, " +
                                                              "Completed, Failed, Cancelled."
                                                             )]
                                                 string? status = null,
                                                 [Description("Optional library identifier")]
                                                 string? library = null,
                                                 [Description("Optional library version")]
                                                 string? version = null,
                                                 [Description("If true (default), also delete each affected " +
                                                              "scrape job's ScrapeAuditLog rows. Ignored for " +
                                                              "background and rescrub jobs."
                                                             )]
                                                 bool includeAudit = true,
                                                 [Description("If true (default), preview without writing.")]
                                                 bool dryRun = true,
                                                 [Description("Optional database profile name")]
                                                 string? profile = null,
                                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);

        var parsedKind = ParseKind(kind);
        var parsedStatus = ParseStatus(status);
        var idList = jobIds?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray() ?? Array.Empty<string>();
        bool hasFilter = idList.Length > 0
                      || parsedStatus.HasValue
                      || !string.IsNullOrWhiteSpace(library)
                      || !string.IsNullOrWhiteSpace(version);

        Task<string> task = (hasFilter, dryRun) switch
            {
                (false, _) => Task.FromResult(JsonSerializer.Serialize(new
                                                                           {
                                                                               Status = NoFilterStatus,
                                                                               Message = NoFilterMessage
                                                                           },
                                                                       smJsonOptions
                                                                      )
                                             ),
                (true, true) => BuildCleanupJobsDryRunAsync(repositoryFactory,
                                                           profile,
                                                           parsedKind,
                                                           idList,
                                                           parsedStatus,
                                                           library,
                                                           version,
                                                           includeAudit,
                                                           ct
                                                          ),
                (true, false) => QueueCleanupJobsAsync(repositoryFactory,
                                                       runner,
                                                       profile,
                                                       parsedKind,
                                                       idList,
                                                       parsedStatus,
                                                       library,
                                                       version,
                                                       includeAudit,
                                                       ct
                                                      )
            };
        string result = await task;
        return result;
    }

    private static async Task<string> QueueCleanupAuditLogJobAsync(string jobId,
                                                                   RepositoryFactory repositoryFactory,
                                                                   IBackgroundJobRunner runner,
                                                                   string? profile,
                                                                   CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new { jobId, profile });
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.CleanupAuditLog,
                                Profile = profile,
                                InputJson = inputJson
                            };

        var bgJobId = await runner.QueueAsync(jobRecord,
                                              async (record, _, jobCt) =>
                                              {
                                                  var auditRepo =
                                                      repositoryFactory.GetScrapeAuditRepository(profile);
                                                  long deleted = await auditRepo.DeleteByJobIdAsync(jobId, jobCt);
                                                  record.ResultJson = JsonSerializer.Serialize(new
                                                               {
                                                                   DryRun = false,
                                                                   JobId = jobId,
                                                                   Deleted = new
                                                                                 {
                                                                                     AuditRows = deleted
                                                                                 },
                                                                   ScrapeJobRowAffected = false
                                                               },
                                                           smJsonOptions
                                                      );
                                              },
                                              ct
                                             );

        return JsonSerializer.Serialize(new { JobId = bgJobId, Status = nameof(ScrapeJobStatus.Queued) },
                                        smJsonOptions
                                       );
    }

    private static async Task<string> BuildCleanupJobsDryRunAsync(RepositoryFactory repositoryFactory,
                                                                  string? profile,
                                                                  JobKind kind,
                                                                  string[] idList,
                                                                  ScrapeJobStatus? parsedStatus,
                                                                  string? library,
                                                                  string? version,
                                                                  bool includeAudit,
                                                                  CancellationToken ct)
    {
        var scrapePreview = await BuildScrapeDryRunAsync(repositoryFactory,
                                                         profile,
                                                         kind,
                                                         idList,
                                                         parsedStatus,
                                                         library,
                                                         version,
                                                         ct
                                                        );
        var backgroundPreview = await BuildBackgroundDryRunAsync(repositoryFactory,
                                                                 profile,
                                                                 kind,
                                                                 idList,
                                                                 parsedStatus,
                                                                 library,
                                                                 version,
                                                                 ct
                                                                );
        var rescrubPreview = await BuildRescrubDryRunAsync(repositoryFactory,
                                                           profile,
                                                           kind,
                                                           idList,
                                                           parsedStatus,
                                                           library,
                                                           version,
                                                           ct
                                                          );

        var preview = new
                          {
                              DryRun = true,
                              Filter = new
                                           {
                                               Kind = kind.ToString(),
                                               JobIds = idList.Length == 0 ? null : idList,
                                               Status = parsedStatus?.ToString(),
                                               Library = library,
                                               Version = version
                                           },
                              WouldDelete = new
                                                {
                                                    ScrapeJobRows = scrapePreview.Count,
                                                    BackgroundJobRows = backgroundPreview.Count,
                                                    RescrubJobRows = rescrubPreview.Count,
                                                    AuditCascade = includeAudit && InScope(kind, JobKind.Scrape)
                                                },
                              SampleScrapeJobs = scrapePreview.Sample,
                              SampleBackgroundJobs = backgroundPreview.Sample,
                              SampleRescrubJobs = rescrubPreview.Sample
                          };
        return JsonSerializer.Serialize(preview, smJsonOptions);
    }

    private static async Task<string> QueueCleanupJobsAsync(RepositoryFactory repositoryFactory,
                                                            IBackgroundJobRunner runner,
                                                            string? profile,
                                                            JobKind kind,
                                                            string[] idList,
                                                            ScrapeJobStatus? parsedStatus,
                                                            string? library,
                                                            string? version,
                                                            bool includeAudit,
                                                            CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new
                                                     {
                                                         kind = kind.ToString(),
                                                         jobIds = idList.Length == 0 ? null : idList,
                                                         status = parsedStatus?.ToString(),
                                                         library,
                                                         version,
                                                         includeAudit,
                                                         profile
                                                     }
                                                );
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.CleanupJobs,
                                Profile = profile,
                                LibraryId = library,
                                Version = version,
                                InputJson = inputJson
                            };

        var bgJobId = await runner.QueueAsync(jobRecord,
                                              async (record, _, jobCt) =>
                                              {
                                                  var deletion = await ApplyCleanupJobsAsync(repositoryFactory,
                                                                            profile,
                                                                            kind,
                                                                            idList,
                                                                            parsedStatus,
                                                                            library,
                                                                            version,
                                                                            includeAudit,
                                                                            jobCt
                                                                       );
                                                  record.ResultJson = JsonSerializer.Serialize(new
                                                               {
                                                                   DryRun = false,
                                                                   Filter = new
                                                                                {
                                                                                    Kind = kind.ToString(),
                                                                                    JobIds = idList.Length == 0
                                                                                                 ? null
                                                                                                 : idList,
                                                                                    Status = parsedStatus?.ToString(),
                                                                                    Library = library,
                                                                                    Version = version
                                                                                },
                                                                   Deleted = new
                                                                                 {
                                                                                     ScrapeJobRows =
                                                                                         deletion.ScrapeJobsDeleted,
                                                                                     BackgroundJobRows =
                                                                                         deletion.BackgroundJobsDeleted,
                                                                                     RescrubJobRows =
                                                                                         deletion.RescrubJobsDeleted,
                                                                                     AuditRows =
                                                                                         deletion.AuditDeleted
                                                                                 }
                                                               },
                                                           smJsonOptions
                                                      );
                                              },
                                              ct
                                             );

        return JsonSerializer.Serialize(new { JobId = bgJobId, Status = nameof(ScrapeJobStatus.Queued) },
                                        smJsonOptions
                                       );
    }

    private static async Task<DryRunSlice> BuildScrapeDryRunAsync(RepositoryFactory repositoryFactory,
                                                                  string? profile,
                                                                  JobKind kind,
                                                                  string[] idList,
                                                                  ScrapeJobStatus? parsedStatus,
                                                                  string? library,
                                                                  string? version,
                                                                  CancellationToken ct)
    {
        var slice = new DryRunSlice(Count: 0, Sample: Array.Empty<object>());
        if (InScope(kind, JobKind.Scrape))
        {
            var repo = repositoryFactory.GetScrapeJobRepository(profile);
            var sample = idList.Length > 0
                             ? await LookupScrapeJobsByIdAsync(repo, idList, ct)
                             : await repo.ListDeleteCandidatesAsync(parsedStatus, library, version, SampleSize, ct);
            long total = idList.Length > 0
                             ? sample.Count
                             : await repo.CountDeleteCandidatesAsync(parsedStatus, library, version, ct);
            slice = new DryRunSlice(total, sample.Take(SampleSize).Select(SerializeScrapeJob).ToArray());
        }

        return slice;
    }

    private static async Task<DryRunSlice> BuildBackgroundDryRunAsync(RepositoryFactory repositoryFactory,
                                                                      string? profile,
                                                                      JobKind kind,
                                                                      string[] idList,
                                                                      ScrapeJobStatus? parsedStatus,
                                                                      string? library,
                                                                      string? version,
                                                                      CancellationToken ct)
    {
        var slice = new DryRunSlice(Count: 0, Sample: Array.Empty<object>());
        if (InScope(kind, JobKind.Background))
        {
            var repo = repositoryFactory.GetBackgroundJobRepository(profile);
            var sample = idList.Length > 0
                             ? await LookupBackgroundJobsByIdAsync(repo, idList, ct)
                             : await repo.ListDeleteCandidatesAsync(parsedStatus, library, version, SampleSize, ct);
            long total = idList.Length > 0
                             ? sample.Count
                             : await repo.CountDeleteCandidatesAsync(parsedStatus, library, version, ct);
            slice = new DryRunSlice(total, sample.Take(SampleSize).Select(SerializeBackgroundJob).ToArray());
        }

        return slice;
    }

    private static async Task<DryRunSlice> BuildRescrubDryRunAsync(RepositoryFactory repositoryFactory,
                                                                   string? profile,
                                                                   JobKind kind,
                                                                   string[] idList,
                                                                   ScrapeJobStatus? parsedStatus,
                                                                   string? library,
                                                                   string? version,
                                                                   CancellationToken ct)
    {
        var slice = new DryRunSlice(Count: 0, Sample: Array.Empty<object>());
        if (InScope(kind, JobKind.Rescrub))
        {
            var repo = repositoryFactory.GetRescrubJobRepository(profile);
            var sample = idList.Length > 0
                             ? await LookupRescrubJobsByIdAsync(repo, idList, ct)
                             : await repo.ListDeleteCandidatesAsync(parsedStatus, library, version, SampleSize, ct);
            long total = idList.Length > 0
                             ? sample.Count
                             : await repo.CountDeleteCandidatesAsync(parsedStatus, library, version, ct);
            slice = new DryRunSlice(total, sample.Take(SampleSize).Select(SerializeRescrubJob).ToArray());
        }

        return slice;
    }

    private static async Task<CleanupJobsApplyResult> ApplyCleanupJobsAsync(RepositoryFactory repositoryFactory,
                                                                            string? profile,
                                                                            JobKind kind,
                                                                            string[] idList,
                                                                            ScrapeJobStatus? parsedStatus,
                                                                            string? library,
                                                                            string? version,
                                                                            bool includeAudit,
                                                                            CancellationToken ct)
    {
        long scrapeDeleted = 0;
        long backgroundDeleted = 0;
        long rescrubDeleted = 0;
        long auditDeleted = 0;

        if (InScope(kind, JobKind.Scrape))
        {
            var (scrape, audit) = await ApplyScrapeCleanupAsync(repositoryFactory,
                                                                profile,
                                                                idList,
                                                                parsedStatus,
                                                                library,
                                                                version,
                                                                includeAudit,
                                                                ct
                                                               );
            scrapeDeleted = scrape;
            auditDeleted = audit;
        }

        if (InScope(kind, JobKind.Background))
            backgroundDeleted = await ApplyBackgroundCleanupAsync(repositoryFactory,
                                                                  profile,
                                                                  idList,
                                                                  parsedStatus,
                                                                  library,
                                                                  version,
                                                                  ct
                                                                 );

        if (InScope(kind, JobKind.Rescrub))
            rescrubDeleted = await ApplyRescrubCleanupAsync(repositoryFactory,
                                                            profile,
                                                            idList,
                                                            parsedStatus,
                                                            library,
                                                            version,
                                                            ct
                                                           );

        return new CleanupJobsApplyResult(scrapeDeleted, backgroundDeleted, rescrubDeleted, auditDeleted);
    }

    private static async Task<(long Jobs, long Audit)> ApplyScrapeCleanupAsync(RepositoryFactory repositoryFactory,
                                                                               string? profile,
                                                                               string[] idList,
                                                                               ScrapeJobStatus? parsedStatus,
                                                                               string? library,
                                                                               string? version,
                                                                               bool includeAudit,
                                                                               CancellationToken ct)
    {
        var repo = repositoryFactory.GetScrapeJobRepository(profile);
        var auditRepo = repositoryFactory.GetScrapeAuditRepository(profile);
        var result = idList.Length > 0
                         ? await ApplyScrapeByIdsAsync(repo, auditRepo, idList, includeAudit, ct)
                         : await ApplyScrapeByFilterAsync(repo,
                                                          auditRepo,
                                                          parsedStatus,
                                                          library,
                                                          version,
                                                          includeAudit,
                                                          ct
                                                         );
        return result;
    }

    private static async Task<(long Jobs, long Audit)> ApplyScrapeByIdsAsync(IScrapeJobRepository repo,
                                                                             IScrapeAuditRepository auditRepo,
                                                                             string[] idList,
                                                                             bool includeAudit,
                                                                             CancellationToken ct)
    {
        long jobs = 0;
        long audit = 0;
        foreach(var id in idList)
        {
            long auditCount = includeAudit ? await auditRepo.DeleteByJobIdAsync(id, ct) : 0;
            audit += auditCount;
            bool removed = await repo.DeleteAsync(id, ct);
            jobs += removed ? 1 : 0;
        }

        return (jobs, audit);
    }

    private static async Task<(long Jobs, long Audit)> ApplyScrapeByFilterAsync(IScrapeJobRepository repo,
                                                                                IScrapeAuditRepository auditRepo,
                                                                                ScrapeJobStatus? parsedStatus,
                                                                                string? library,
                                                                                string? version,
                                                                                bool includeAudit,
                                                                                CancellationToken ct)
    {
        long audit = 0;
        if (includeAudit)
        {
            var matches = await repo.ListDeleteCandidatesAsync(parsedStatus,
                                                               library,
                                                               version,
                                                               AllMatchesLimit,
                                                               ct
                                                              );
            foreach(var job in matches)
                audit += await auditRepo.DeleteByJobIdAsync(job.Id, ct);
        }

        long jobs = await repo.DeleteManyAsync(parsedStatus, library, version, ct);
        return (jobs, audit);
    }

    private static async Task<long> ApplyBackgroundCleanupAsync(RepositoryFactory repositoryFactory,
                                                                string? profile,
                                                                string[] idList,
                                                                ScrapeJobStatus? parsedStatus,
                                                                string? library,
                                                                string? version,
                                                                CancellationToken ct)
    {
        var repo = repositoryFactory.GetBackgroundJobRepository(profile);
        long jobs = idList.Length > 0
                        ? await DeleteBackgroundByIdsAsync(repo, idList, ct)
                        : await repo.DeleteManyAsync(parsedStatus, library, version, ct);
        return jobs;
    }

    private static async Task<long> DeleteBackgroundByIdsAsync(IBackgroundJobRepository repo,
                                                               string[] idList,
                                                               CancellationToken ct)
    {
        long jobs = 0;
        foreach(var id in idList)
        {
            bool removed = await repo.DeleteAsync(id, ct);
            jobs += removed ? 1 : 0;
        }

        return jobs;
    }

    private static async Task<long> ApplyRescrubCleanupAsync(RepositoryFactory repositoryFactory,
                                                             string? profile,
                                                             string[] idList,
                                                             ScrapeJobStatus? parsedStatus,
                                                             string? library,
                                                             string? version,
                                                             CancellationToken ct)
    {
        var repo = repositoryFactory.GetRescrubJobRepository(profile);
        long jobs = idList.Length > 0
                        ? await DeleteRescrubByIdsAsync(repo, idList, ct)
                        : await repo.DeleteManyAsync(parsedStatus, library, version, ct);
        return jobs;
    }

    private static async Task<long> DeleteRescrubByIdsAsync(IRescrubJobRepository repo,
                                                            string[] idList,
                                                            CancellationToken ct)
    {
        long jobs = 0;
        foreach(var id in idList)
        {
            bool removed = await repo.DeleteAsync(id, ct);
            jobs += removed ? 1 : 0;
        }

        return jobs;
    }

    private static async Task<IReadOnlyList<ScrapeJobRecord>> LookupScrapeJobsByIdAsync(IScrapeJobRepository repo,
        string[] idList,
        CancellationToken ct)
    {
        var lookups = await Task.WhenAll(idList.Select(id => repo.GetAsync(id, ct)));
        IReadOnlyList<ScrapeJobRecord> result = lookups.Where(j => j != null).Cast<ScrapeJobRecord>().ToList();
        return result;
    }

    private static async Task<IReadOnlyList<BackgroundJobRecord>> LookupBackgroundJobsByIdAsync(
        IBackgroundJobRepository repo,
        string[] idList,
        CancellationToken ct)
    {
        var lookups = await Task.WhenAll(idList.Select(id => repo.GetAsync(id, ct)));
        IReadOnlyList<BackgroundJobRecord> result = lookups.Where(j => j != null).Cast<BackgroundJobRecord>().ToList();
        return result;
    }

    private static async Task<IReadOnlyList<RescrubJobRecord>> LookupRescrubJobsByIdAsync(IRescrubJobRepository repo,
        string[] idList,
        CancellationToken ct)
    {
        var lookups = await Task.WhenAll(idList.Select(id => repo.GetAsync(id, ct)));
        IReadOnlyList<RescrubJobRecord> result = lookups.Where(j => j != null).Cast<RescrubJobRecord>().ToList();
        return result;
    }

    private static object SerializeScrapeJob(ScrapeJobRecord job) => new
                                                                         {
                                                                             job.Id,
                                                                             Status = job.Status.ToString(),
                                                                             LibraryId = job.Job.LibraryId,
                                                                             Version = job.Job.Version,
                                                                             job.CreatedAt,
                                                                             job.CompletedAt
                                                                         };

    private static object SerializeBackgroundJob(BackgroundJobRecord job) => new
                                                                                 {
                                                                                     job.Id,
                                                                                     job.JobType,
                                                                                     Status = job.Status.ToString(),
                                                                                     job.LibraryId,
                                                                                     job.Version,
                                                                                     job.CreatedAt,
                                                                                     job.CompletedAt
                                                                                 };

    private static object SerializeRescrubJob(RescrubJobRecord job) => new
                                                                           {
                                                                               job.Id,
                                                                               Status = job.Status.ToString(),
                                                                               job.LibraryId,
                                                                               job.Version,
                                                                               job.CreatedAt,
                                                                               job.CompletedAt
                                                                           };

    private static bool InScope(JobKind selected, JobKind candidate) =>
        selected == JobKind.All || selected == candidate;

    private static JobKind ParseKind(string? raw)
    {
        var result = JobKind.All;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            bool parsedOk = Enum.TryParse<JobKind>(raw, ignoreCase: true, out var parsed);
            if (!parsedOk)
                throw new ArgumentException(string.Format(UnknownKindMessage, raw), nameof(raw));
            result = parsed;
        }

        return result;
    }

    private static ScrapeJobStatus? ParseStatus(string? raw)
    {
        ScrapeJobStatus? result = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            bool parsedOk = Enum.TryParse<ScrapeJobStatus>(raw, ignoreCase: true, out var parsed);
            if (!parsedOk)
                throw new ArgumentException(string.Format(UnknownStatusMessage, raw), nameof(raw));
            result = parsed;
        }

        return result;
    }

    private sealed record CleanupJobsApplyResult(long ScrapeJobsDeleted,
                                                 long BackgroundJobsDeleted,
                                                 long RescrubJobsDeleted,
                                                 long AuditDeleted);

    private sealed record DryRunSlice(long Count, IReadOnlyList<object> Sample);

    private const int SampleSize = 20;
    private const int AllMatchesLimit = 100_000;
    private const string NoFilterStatus = "NoFilter";
    private const string NoFilterMessage = "At least one of jobIds, status, library, or version must be supplied. Refusing to delete every job row.";
    private const string UnknownStatusMessage = "Unknown status '{0}'. Expected one of: Queued, Running, Completed, Failed, Cancelled.";
    private const string UnknownKindMessage = "Unknown kind '{0}'. Expected one of: scrape, background, rescrub, all.";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
