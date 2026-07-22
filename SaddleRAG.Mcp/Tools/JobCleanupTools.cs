// JobCleanupTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     Manual cleanup tools for ScrapeAuditLog and the unified
///     <c>jobs</c> collection. Job rows auto-purge after 30 days via a
///     Mongo TTL index; these tools cover early eviction and explicit-id
///     removal. Both default to dryRun=true. Apply paths queue a
///     background job and return the JobId immediately.
/// </summary>
[McpServerToolType]
public static class JobCleanupTools
{
    private sealed record CleanupJobsApplyResult(long JobsDeleted, long AuditDeleted);

    private sealed record DryRunSlice(long Count, IReadOnlyList<object> Sample);

    [McpServerTool(Name = "cleanup_audit_log")]
    [Description("Manually purge ScrapeAuditLog rows for a single scrape job. Useful when " +
                 "a runaway crawl has left a huge audit log behind and you want to evict " +
                 "it before the 30-day TTL fires. Defaults to dryRun=true — preview the " +
                 "row count before passing dryRun=false to apply. dryRun=false returns " +
                 "{ JobId, Status: 'Queued' } immediately; poll get_job_status for the outcome. " +
                 "Does NOT delete the jobs row itself — use cleanup_jobs for that."
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
                                  JobRowAffected = false
                              };
            result = JsonSerializer.Serialize(preview, smJsonOptions);
        }
        else
            result = await QueueCleanupAuditLogJobAsync(jobId, repositoryFactory, runner, profile, ct);

        return result;
    }

    [McpServerTool(Name = "cleanup_jobs")]
    [Description("Manually delete rows from the unified jobs collection. Filter by " +
                 "explicit id list, status (Queued/Running/Completed/Failed/Cancelled), " +
                 "jobType (Scrape/Rechunk/Rescrub/Reembed/RenameLibrary/…), library, and/or " +
                 "version. At least one of jobIds/jobType/status/library/version must be " +
                 "supplied — calls with no filter return zero. For scrape jobs, cascades " +
                 "to ScrapeAuditLog by default (includeAudit=true). Defaults to dryRun=true. " +
                 "dryRun=false returns { JobId, Status: 'Queued' } immediately; poll " +
                 "get_job_status for the outcome."
                )]
    public static Task<string> CleanupJobsFromMcp(RepositoryFactory repositoryFactory,
                                                  [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                  IBackgroundJobRunner runner,
                                                  [Description("Optional job type filter — enum name (e.g. Scrape, Rescrub, " +
                                                               "Reembed, Rechunk) or legacy snake_case (rechunk, rename_library, …). " +
                                                               "Omit to match every type."
                                                              )]
                                                  string? jobType = null,
                                                  [Description("Optional explicit list of job ids. When set, " +
                                                               "jobType/status/library/version are ignored. Each id is " +
                                                               "looked up in the unified jobs collection."
                                                              )]
                                                  JsonElement? jobIds = null,
                                                  [Description("Optional status filter: Queued, Running, " +
                                                               "Completed, Failed, Cancelled."
                                                              )]
                                                  string? status = null,
                                                  [Description("Optional library identifier")]
                                                  string? library = null,
                                                  [Description("Optional library version")]
                                                  string? version = null,
                                                  [Description("If true (default), also delete each affected " +
                                                               "scrape job's ScrapeAuditLog rows. Ignored for non-scrape jobs."
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

        string[]? parsedJobIds = McpStringArrayArgumentParser.Parse(jobIds, nameof(jobIds));
        Task<string> result = CleanupJobs(repositoryFactory,
                                          runner,
                                          jobType,
                                          parsedJobIds,
                                          status,
                                          library,
                                          version,
                                          includeAudit,
                                          dryRun,
                                          profile,
                                          ct
                                         );
        return result;
    }

    public static async Task<string> CleanupJobs(RepositoryFactory repositoryFactory,
                                                 IBackgroundJobRunner runner,
                                                 string? jobType = null,
                                                 string[]? jobIds = null,
                                                 string? status = null,
                                                 string? library = null,
                                                 string? version = null,
                                                 bool includeAudit = true,
                                                 bool dryRun = true,
                                                 string? profile = null,
                                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);

        var parsedType = ParseJobType(jobType);
        var parsedStatus = ParseStatus(status);
        var idList = jobIds?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray() ?? [];
        bool hasFilter = idList.Length > 0 ||
                         parsedType.HasValue ||
                         parsedStatus.HasValue ||
                         !string.IsNullOrWhiteSpace(library) ||
                         !string.IsNullOrWhiteSpace(version);

        var task = (hasFilter, dryRun) switch
            {
                (false, var _) => Task.FromResult(JsonSerializer.Serialize(new
                                                                               {
                                                                                   Status = NoFilterStatus,
                                                                                   Message = NoFilterMessage
                                                                               },
                                                                           smJsonOptions
                                                                          )
                                                 ),
                (true, true) => BuildCleanupJobsDryRunAsync(repositoryFactory,
                                                            profile,
                                                            parsedType,
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
                                                       parsedType,
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
                                                                   JobRowAffected = false
                                                               },
                                                           smJsonOptions
                                                      );
                                              },
                                              ct
                                             );

        return JsonSerializer.Serialize(new { JobId = bgJobId, Status = nameof(JobStatus.Queued) },
                                        smJsonOptions
                                       );
    }

    private static async Task<string> BuildCleanupJobsDryRunAsync(RepositoryFactory repositoryFactory,
                                                                  string? profile,
                                                                  JobType? parsedType,
                                                                  string[] idList,
                                                                  JobStatus? parsedStatus,
                                                                  string? library,
                                                                  string? version,
                                                                  bool includeAudit,
                                                                  CancellationToken ct)
    {
        var repo = repositoryFactory.GetJobRepository(profile);
        var slice = await BuildDryRunSliceAsync(repo,
                                                parsedType,
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
                                               JobType = parsedType?.ToString(),
                                               JobIds = idList.Length == 0 ? null : idList,
                                               Status = parsedStatus?.ToString(),
                                               Library = library,
                                               Version = version
                                           },
                              WouldDelete = new
                                                {
                                                    JobRows = slice.Count,
                                                    AuditCascade = includeAudit && CouldCascadeAudit(parsedType)
                                                },
                              SampleJobs = slice.Sample
                          };
        return JsonSerializer.Serialize(preview, smJsonOptions);
    }

    private static async Task<string> QueueCleanupJobsAsync(RepositoryFactory repositoryFactory,
                                                            IBackgroundJobRunner runner,
                                                            string? profile,
                                                            JobType? parsedType,
                                                            string[] idList,
                                                            JobStatus? parsedStatus,
                                                            string? library,
                                                            string? version,
                                                            bool includeAudit,
                                                            CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new
                                                     {
                                                         jobType = parsedType?.ToString(),
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
                                                                          parsedType,
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
                                                                                    JobType = parsedType?.ToString(),
                                                                                    JobIds = idList.Length == 0
                                                                                        ? null
                                                                                        : idList,
                                                                                    Status = parsedStatus?.ToString(),
                                                                                    Library = library,
                                                                                    Version = version
                                                                                },
                                                                   Deleted = new
                                                                                 {
                                                                                     JobRows =
                                                                                         deletion.JobsDeleted,
                                                                                     AuditRows =
                                                                                         deletion.AuditDeleted
                                                                                 }
                                                               },
                                                           smJsonOptions
                                                      );
                                              },
                                              ct
                                             );

        return JsonSerializer.Serialize(new { JobId = bgJobId, Status = nameof(JobStatus.Queued) },
                                        smJsonOptions
                                       );
    }

    private static async Task<DryRunSlice> BuildDryRunSliceAsync(IJobRepository repo,
                                                                  JobType? parsedType,
                                                                  string[] idList,
                                                                  JobStatus? parsedStatus,
                                                                  string? library,
                                                                  string? version,
                                                                  CancellationToken ct)
    {
        IReadOnlyList<JobRecord> sample;
        long total;
        if (idList.Length > 0)
        {
            sample = await LookupJobsByIdAsync(repo, idList, ct);
            total = sample.Count;
        }
        else
        {
            sample = await repo.ListDeleteCandidatesAsync(parsedType,
                                                          parsedStatus,
                                                          library,
                                                          version,
                                                          completedBefore: null,
                                                          SampleSize,
                                                          ct
                                                         );
            total = await repo.CountDeleteCandidatesAsync(parsedType,
                                                          parsedStatus,
                                                          library,
                                                          version,
                                                          completedBefore: null,
                                                          ct
                                                         );
        }

        var serialized = sample.Take(SampleSize).Select(SerializeJob).ToArray();
        return new DryRunSlice(total, serialized);
    }

    private static async Task<CleanupJobsApplyResult> ApplyCleanupJobsAsync(RepositoryFactory repositoryFactory,
                                                                            string? profile,
                                                                            JobType? parsedType,
                                                                            string[] idList,
                                                                            JobStatus? parsedStatus,
                                                                            string? library,
                                                                            string? version,
                                                                            bool includeAudit,
                                                                            CancellationToken ct)
    {
        var repo = repositoryFactory.GetJobRepository(profile);
        var auditRepo = repositoryFactory.GetScrapeAuditRepository(profile);
        long jobs;
        long audit = 0;

        if (idList.Length > 0)
            (jobs, audit) = await ApplyByIdsAsync(repo, auditRepo, idList, includeAudit, ct);
        else
        {
            if (includeAudit && CouldCascadeAudit(parsedType))
            {
                var matches = await repo.ListDeleteCandidatesAsync(parsedType,
                                                                    parsedStatus,
                                                                    library,
                                                                    version,
                                                                    completedBefore: null,
                                                                    AllMatchesLimit,
                                                                    ct
                                                                   );
                foreach(var match in matches.Where(m => m.JobType == JobType.Scrape))
                    audit += await auditRepo.DeleteByJobIdAsync(match.Id, ct);
            }

            jobs = await repo.DeleteManyAsync(parsedType,
                                              parsedStatus,
                                              library,
                                              version,
                                              completedBefore: null,
                                              ct
                                             );
        }

        return new CleanupJobsApplyResult(jobs, audit);
    }

    private static async Task<(long Jobs, long Audit)> ApplyByIdsAsync(IJobRepository repo,
                                                                       IScrapeAuditRepository auditRepo,
                                                                       string[] idList,
                                                                       bool includeAudit,
                                                                       CancellationToken ct)
    {
        long jobs = 0;
        long audit = 0;
        foreach(var id in idList)
        {
            var record = await repo.GetAsync(id, ct);
            if (record is not null)
            {
                if (includeAudit && record.JobType == JobType.Scrape)
                    audit += await auditRepo.DeleteByJobIdAsync(id, ct);
                bool removed = await repo.DeleteAsync(id, ct);
                jobs += removed ? 1 : 0;
            }
        }

        return (jobs, audit);
    }

    private static async Task<IReadOnlyList<JobRecord>> LookupJobsByIdAsync(IJobRepository repo,
                                                                            string[] idList,
                                                                            CancellationToken ct)
    {
        var lookups = await Task.WhenAll(idList.Select(id => repo.GetAsync(id, ct)));
        IReadOnlyList<JobRecord> result = lookups.Where(j => j is not null).Cast<JobRecord>().ToList();
        return result;
    }

    private static object SerializeJob(JobRecord job) => new
                                                              {
                                                                  job.Id,
                                                                  JobType = job.JobType.ToString(),
                                                                  Status = job.Status.ToString(),
                                                                  job.LibraryId,
                                                                  job.Version,
                                                                  job.CreatedAt,
                                                                  job.CompletedAt
                                                              };

    private static bool CouldCascadeAudit(JobType? parsedType) =>
        parsedType is null or JobType.Scrape;

    private static JobType? ParseJobType(string? raw)
    {
        JobType? result = null;
        if (!string.IsNullOrWhiteSpace(raw))
            result = LegacyJobTypeToEnum(raw) ?? ParseEnumStrict(raw);

        return result;
    }

    private static JobType ParseEnumStrict(string raw) =>
        Enum.TryParse<JobType>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(string.Format(UnknownJobTypeMessage, raw), nameof(raw));

    private static JobType? LegacyJobTypeToEnum(string legacyType) => legacyType switch
        {
            "scrape"                     => JobType.Scrape,
            "rescrub"                    => JobType.Rescrub,
            "reextract"                  => JobType.Rescrub,
            "reembed"                    => JobType.Reembed,
            "dryrun_scrape"              => JobType.DryRunScrape,
            "rechunk"                    => JobType.Rechunk,
            "rename_library"             => JobType.RenameLibrary,
            "rename_version"             => JobType.RenameVersion,
            "delete_version"             => JobType.DeleteVersion,
            "delete_library"             => JobType.DeleteLibrary,
            "index_project_dependencies" => JobType.IndexProjectDependencies,
            "submit_url_correction"      => JobType.SubmitUrlCorrection,
            "cleanup_audit_log"          => JobType.CleanupAuditLog,
            "cleanup_jobs"               => JobType.CleanupJobs,
            "cleanup_orphans"            => JobType.CleanupOrphans,
            var _                        => null
        };

    private static JobStatus? ParseStatus(string? raw)
    {
        JobStatus? result = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            bool parsedOk = Enum.TryParse<JobStatus>(raw, ignoreCase: true, out var parsed);
            if (!parsedOk)
                throw new ArgumentException(string.Format(UnknownStatusMessage, raw), nameof(raw));
            result = parsed;
        }

        return result;
    }

    private const int SampleSize = 20;
    private const int AllMatchesLimit = 100_000;
    private const string NoFilterStatus = "NoFilter";

    private const string NoFilterMessage =
        "At least one of jobIds, jobType, status, library, or version must be supplied. Refusing to delete every job row.";

    private const string UnknownStatusMessage =
        "Unknown status '{0}'. Expected one of: Queued, Running, Completed, Failed, Cancelled.";

    private const string UnknownJobTypeMessage =
        "Unknown jobType '{0}'. Expected an enum name (e.g. Scrape, Rechunk, Reembed) or legacy snake_case (e.g. rechunk, rename_library).";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
