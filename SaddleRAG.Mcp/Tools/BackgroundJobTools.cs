// BackgroundJobTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for polling the unified <c>jobs</c> collection:
///     <c>get_job_status</c> and <c>list_jobs</c>. With the four-collection
///     to one consolidation these tools now serve every job type — scrape,
///     reextract, reembed, and the original background-job set (rechunk,
///     rename_library, delete_version, delete_library, dryrun_scrape,
///     index_project_dependencies, submit_url_correction, cleanup_*).
/// </summary>
[McpServerToolType]
public static class BackgroundJobTools
{
    [McpServerTool(Name = "get_job_status")]
    [Description("Check the status of any job by its id — works for scrape, reextract, " +
                 "reembed, rechunk, rename_library, delete_version, delete_library, dryrun_scrape, " +
                 "index_project_dependencies, submit_url_correction, and the cleanup_* family. " +
                 "Status values: Queued, Running (ItemsProcessed/ItemsTotal show progress where applicable), " +
                 "Completed (Result contains the full output), Failed (check ErrorMessage), Cancelled. " +
                 "Completed rechunk jobs may also include BoundaryHint ('rechunk_library may help' or 'rechunk_library recommended'); act on that before calling search_docs. " +
                 "Poll at 10–30s intervals. Job id comes from the tool that queued the operation."
                )]
    public static async Task<string> GetJobStatus(RepositoryFactory repositoryFactory,
                                                  [Description("Job id returned by any tool that queues work (scrape_docs, reembed_library, rechunk_library, rename_library, …)."
                                                              )]
                                                  string jobId,
                                                  [Description("Optional database profile name (use list_profiles to discover)."
                                                              )]
                                                  string? profile = null,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = repositoryFactory.GetJobRepository(profile);
        var job = await jobRepo.GetAsync(jobId, ct);

        string result;
        if (job is null)
            result = $"No job found with id '{jobId}'.";
        else
        {
            object? parsedResult = null;
            string? boundaryHint = null;

            if (job.ResultJson is not null)
            {
                try
                {
                    parsedResult = JsonSerializer.Deserialize<JsonElement>(job.ResultJson, smJsonOptions);
                    boundaryHint = ComputeBoundaryHint(job.JobType, parsedResult);
                }
                catch(JsonException)
                {
                    parsedResult = null;
                }
            }

            var response = new
                               {
                                   job.Id,
                                   JobType = job.JobType.ToString(),
                                   Status = job.Status.ToString(),
                                   job.PipelineState,
                                   job.Profile,
                                   job.LibraryId,
                                   job.Version,
                                   job.ItemsProcessed,
                                   job.ItemsTotal,
                                   job.ItemsLabel,
                                   job.ErrorMessage,
                                   Result = parsedResult,
                                   BoundaryHint = boundaryHint,
                                   job.CreatedAt,
                                   job.StartedAt,
                                   job.CompletedAt,
                                   job.LastProgressAt,
                                   job.CancelledAt
                               };

            result = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return result;
    }

    private static string? ComputeBoundaryHint(JobType jobType, object? parsedResult)
    {
        string? hint = null;
        if (jobType == JobType.Rechunk && parsedResult is JsonElement root &&
            root.TryGetProperty(ResultJsonKeyBoundaryIssues, out var boundaryIssuesEl) &&
            root.TryGetProperty(ResultJsonKeyProcessed, out var processedEl) &&
            boundaryIssuesEl.TryGetInt32(out int boundaryIssues) &&
            processedEl.TryGetInt32(out int processed) &&
            processed > 0)
        {
            double pct = PercentMultiplier * boundaryIssues / processed;
            hint = pct switch
                {
                    >= BoundaryThresholdHigh => BoundaryHintRecommended,
                    >= BoundaryThresholdMedium => BoundaryHintMayHelp,
                    var _ => null
                };
        }

        return hint;
    }

    [McpServerTool(Name = "list_jobs")]
    [Description("List recent jobs from the unified queue, most recent first. " +
                 "Returns every job type — scrape, reextract, reembed, rechunk, rename_library, " +
                 "delete_version, delete_library, dryrun_scrape, index_project_dependencies, " +
                 "submit_url_correction, and the cleanup_* family. Filter by jobType to narrow " +
                 "results. Use get_job_status(jobId) to poll a specific job's full state. " +
                 "Type-specific list tools (list_scrape_jobs, list_reextract_jobs, list_reembed_jobs) " +
                 "remain available and project type-specific fields."
                )]
    public static async Task<string> ListJobs(RepositoryFactory repositoryFactory,
                                              [Description("Optional job type filter. Accepts enum names (Scrape, Rechunk, " +
                                                           "Rescrub, Reembed, RenameLibrary, DeleteVersion, DeleteLibrary, " +
                                                           "DryRunScrape, IndexProjectDependencies, SubmitUrlCorrection, " +
                                                           "CleanupAuditLog, CleanupJobs, CleanupOrphans) and legacy snake_case " +
                                                           "(rechunk, rename_library, …). Omit to list all types."
                                                          )]
                                              string? jobType = null,
                                              [Description("Maximum number of jobs to return. Defaults to 20.")]
                                              int limit = 20,
                                              [Description("Optional database profile name (use list_profiles to discover)."
                                                          )]
                                              string? profile = null,
                                              CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        JobType? parsedType = ParseJobType(jobType);
        var jobRepo = repositoryFactory.GetJobRepository(profile);
        var jobs = await jobRepo.ListRecentAsync(parsedType, limit, ct);

        var items = jobs.Select(j => new
                                         {
                                             j.Id,
                                             Status = j.Status.ToString(),
                                             JobType = j.JobType.ToString(),
                                             j.LibraryId,
                                             j.Version,
                                             j.ItemsProcessed,
                                             j.ItemsTotal,
                                             j.ItemsLabel,
                                             j.CreatedAt,
                                             j.CompletedAt
                                         }
                               );

        return JsonSerializer.Serialize(items, smJsonOptions);
    }

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
            "delete_version"             => JobType.DeleteVersion,
            "delete_library"             => JobType.DeleteLibrary,
            "index_project_dependencies" => JobType.IndexProjectDependencies,
            "submit_url_correction"      => JobType.SubmitUrlCorrection,
            "cleanup_audit_log"          => JobType.CleanupAuditLog,
            "cleanup_jobs"               => JobType.CleanupJobs,
            "cleanup_orphans"            => JobType.CleanupOrphans,
            var _                        => null
        };

    private const string ResultJsonKeyBoundaryIssues = "BoundaryIssues";
    private const string ResultJsonKeyProcessed = "Processed";
    private const double PercentMultiplier = 100.0;
    private const double BoundaryThresholdHigh = 10.0;
    private const double BoundaryThresholdMedium = 5.0;
    private const string BoundaryHintRecommended = "rechunk_library recommended";
    private const string BoundaryHintMayHelp = "rechunk_library may help";
    private const string UnknownJobTypeMessage =
        "Unknown jobType '{0}'. Expected an enum name (e.g. Scrape, Rechunk, Reembed) or legacy snake_case (e.g. rechunk, rename_library).";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
