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
///     Manual cleanup tools for ScrapeAuditLog and ScrapeJobs. Audit rows
///     auto-purge after 30 days via a Mongo TTL index; ScrapeJobs rows are
///     never auto-purged. These tools cover early eviction (before TTL
///     fires) and ScrapeJobs row removal — both opt-in, both dryRun=true
///     by default. Apply paths queue a background job and return the
///     JobId immediately.
/// </summary>
[McpServerToolType]
public static class JobCleanupTools
{
    [McpServerTool(Name = "cleanup_audit_log")]
    [Description("Manually purge ScrapeAuditLog rows for a single scrape job. Useful when " +
                 "a runaway crawl has left a huge audit log behind and you want to evict " +
                 "it before the 30-day TTL fires. Defaults to dryRun=true — preview the " +
                 "row count before passing dryRun=false to apply. dryRun=false returns " +
                 "{ JobId, Status: 'Queued' } immediately; poll get_job_status for the outcome. " +
                 "Does NOT delete the ScrapeJobs row itself — use delete_scrape_jobs for that."
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

    [McpServerTool(Name = "delete_scrape_jobs")]
    [Description("Manually delete ScrapeJobs rows by explicit id list, by status (Failed/" +
                 "Cancelled/Completed/Running/Queued), by library, and/or by version. At " +
                 "least one of jobIds/status/library/version must be supplied — calls " +
                 "with no filter return zero. Cascades to ScrapeAuditLog by default " +
                 "(includeAudit=true). Defaults to dryRun=true — preview the matching " +
                 "rows before passing dryRun=false. dryRun=false returns { JobId, Status: " +
                 "'Queued' } immediately; poll get_job_status for the outcome."
                )]
    public static async Task<string> DeleteScrapeJobs(RepositoryFactory repositoryFactory,
                                                      [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                      IBackgroundJobRunner runner,
                                                      [Description("Optional explicit list of job ids. When set, " +
                                                                   "status/library/version are ignored."
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
                                                                   "job's ScrapeAuditLog rows."
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
                (true, true) => BuildDeleteJobsDryRunAsync(repositoryFactory,
                                                          profile,
                                                          idList,
                                                          parsedStatus,
                                                          library,
                                                          version,
                                                          includeAudit,
                                                          ct
                                                         ),
                (true, false) => QueueDeleteScrapeJobsAsync(repositoryFactory,
                                                            runner,
                                                            profile,
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

    private static async Task<string> BuildDeleteJobsDryRunAsync(RepositoryFactory repositoryFactory,
                                                                 string? profile,
                                                                 string[] idList,
                                                                 ScrapeJobStatus? parsedStatus,
                                                                 string? library,
                                                                 string? version,
                                                                 bool includeAudit,
                                                                 CancellationToken ct)
    {
        var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
        var (jobsToDelete, totalCount) = await ResolveTargetJobsAsync(jobRepo,
                                                                      idList,
                                                                      parsedStatus,
                                                                      library,
                                                                      version,
                                                                      ct
                                                                     );

        var sample = jobsToDelete.Take(SampleSize)
                                 .Select(j => new
                                                  {
                                                      j.Id,
                                                      Status = j.Status.ToString(),
                                                      LibraryId = j.Job.LibraryId,
                                                      Version = j.Job.Version,
                                                      j.CreatedAt,
                                                      j.CompletedAt
                                                  })
                                 .ToArray();

        var preview = new
                          {
                              DryRun = true,
                              Filter = new
                                           {
                                               JobIds = idList.Length == 0 ? null : idList,
                                               Status = parsedStatus?.ToString(),
                                               Library = library,
                                               Version = version
                                           },
                              WouldDelete = new
                                                {
                                                    ScrapeJobRows = totalCount,
                                                    AuditCascade = includeAudit
                                                },
                              SampleJobs = sample
                          };
        return JsonSerializer.Serialize(preview, smJsonOptions);
    }

    private static async Task<string> QueueDeleteScrapeJobsAsync(RepositoryFactory repositoryFactory,
                                                                 IBackgroundJobRunner runner,
                                                                 string? profile,
                                                                 string[] idList,
                                                                 ScrapeJobStatus? parsedStatus,
                                                                 string? library,
                                                                 string? version,
                                                                 bool includeAudit,
                                                                 CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new
                                                     {
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
                                JobType = BackgroundJobTypes.DeleteScrapeJobs,
                                Profile = profile,
                                LibraryId = library,
                                Version = version,
                                InputJson = inputJson
                            };

        var bgJobId = await runner.QueueAsync(jobRecord,
                                              async (record, _, jobCt) =>
                                              {
                                                  var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
                                                  var auditRepo =
                                                      repositoryFactory.GetScrapeAuditRepository(profile);
                                                  var deletion = await ApplyDeleteScrapeJobsAsync(jobRepo,
                                                                            auditRepo,
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
                                                                                         deletion.JobsDeleted,
                                                                                     AuditRows = deletion.AuditDeleted
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

    private static async Task<(IReadOnlyList<ScrapeJobRecord> Jobs, long TotalCount)> ResolveTargetJobsAsync(
        IScrapeJobRepository jobRepo,
        string[] idList,
        ScrapeJobStatus? parsedStatus,
        string? library,
        string? version,
        CancellationToken ct)
    {
        IReadOnlyList<ScrapeJobRecord> jobs;
        long totalCount;
        if (idList.Length > 0)
        {
            var lookups = await Task.WhenAll(idList.Select(id => jobRepo.GetAsync(id, ct)));
            jobs = lookups.Where(j => j != null).Cast<ScrapeJobRecord>().ToList();
            totalCount = jobs.Count;
        }
        else
        {
            jobs = await jobRepo.ListDeleteCandidatesAsync(parsedStatus,
                                                          library,
                                                          version,
                                                          SampleSize,
                                                          ct
                                                         );
            totalCount = await jobRepo.CountDeleteCandidatesAsync(parsedStatus,
                                                                  library,
                                                                  version,
                                                                  ct
                                                                 );
        }

        return (jobs, totalCount);
    }

    private static async Task<DeleteScrapeJobsApplyResult> ApplyDeleteScrapeJobsAsync(
        IScrapeJobRepository jobRepo,
        IScrapeAuditRepository auditRepo,
        string[] idList,
        ScrapeJobStatus? parsedStatus,
        string? library,
        string? version,
        bool includeAudit,
        CancellationToken ct)
    {
        var deletion = idList.Length > 0
                           ? await DeleteByExplicitIdsAsync(jobRepo, auditRepo, idList, includeAudit, ct)
                           : await DeleteByFilterAsync(jobRepo,
                                                       auditRepo,
                                                       parsedStatus,
                                                       library,
                                                       version,
                                                       includeAudit,
                                                       ct
                                                      );
        return deletion;
    }

    private static async Task<DeleteScrapeJobsApplyResult> DeleteByExplicitIdsAsync(IScrapeJobRepository jobRepo,
        IScrapeAuditRepository auditRepo,
        string[] idList,
        bool includeAudit,
        CancellationToken ct)
    {
        long auditDeleted = 0;
        long jobsDeleted = 0;
        foreach(var id in idList)
        {
            long auditCount = includeAudit ? await auditRepo.DeleteByJobIdAsync(id, ct) : 0;
            auditDeleted += auditCount;
            bool removed = await jobRepo.DeleteAsync(id, ct);
            jobsDeleted += removed ? 1 : 0;
        }

        return new DeleteScrapeJobsApplyResult(jobsDeleted, auditDeleted);
    }

    private static async Task<DeleteScrapeJobsApplyResult> DeleteByFilterAsync(IScrapeJobRepository jobRepo,
        IScrapeAuditRepository auditRepo,
        ScrapeJobStatus? parsedStatus,
        string? library,
        string? version,
        bool includeAudit,
        CancellationToken ct)
    {
        long auditDeleted = includeAudit
                                ? await CascadeAuditByFilterAsync(jobRepo, auditRepo, parsedStatus, library, version, ct)
                                : 0;
        long jobsDeleted = await jobRepo.DeleteManyAsync(parsedStatus, library, version, ct);
        return new DeleteScrapeJobsApplyResult(jobsDeleted, auditDeleted);
    }

    private static async Task<long> CascadeAuditByFilterAsync(IScrapeJobRepository jobRepo,
                                                              IScrapeAuditRepository auditRepo,
                                                              ScrapeJobStatus? parsedStatus,
                                                              string? library,
                                                              string? version,
                                                              CancellationToken ct)
    {
        var matches = await jobRepo.ListDeleteCandidatesAsync(parsedStatus,
                                                              library,
                                                              version,
                                                              AllMatchesLimit,
                                                              ct
                                                             );
        long total = 0;
        foreach(var job in matches)
            total += await auditRepo.DeleteByJobIdAsync(job.Id, ct);

        return total;
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

    private sealed record DeleteScrapeJobsApplyResult(long JobsDeleted, long AuditDeleted);

    private const int SampleSize = 20;
    private const int AllMatchesLimit = 100_000;
    private const string NoFilterStatus = "NoFilter";
    private const string NoFilterMessage = "At least one of jobIds, status, library, or version must be supplied. Refusing to delete every ScrapeJobs row.";
    private const string UnknownStatusMessage = "Unknown status '{0}'. Expected one of: Queued, Running, Completed, Failed, Cancelled.";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
