// BackgroundJobTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for polling generic background jobs: get_job_status and list_jobs.
///     Covers rechunk, rename_library, delete_version, delete_library,
///     dryrun_scrape, index_project_dependencies, and submit_url_correction.
/// </summary>
[McpServerToolType]
public static class BackgroundJobTools
{
    [McpServerTool(Name = "get_job_status")]
    [Description("Check the status of a background job by its id. " +
                 "Covers: rechunk_library, rename_library (apply), delete_version (apply), " +
                 "delete_library (apply), dryrun_scrape, index_project_dependencies, " +
                 "submit_url_correction (apply). " +
                 "Status values: Queued, Running (ItemsProcessed/ItemsTotal show progress where applicable), " +
                 "Completed (Result contains the full output), Failed (check ErrorMessage), Cancelled. " +
                 "Poll at 10–30s intervals. " +
                 "Job id comes from the tool that queued the operation."
                )]
    public static async Task<string> GetJobStatus(RepositoryFactory repositoryFactory,
                                                  [Description("Job id returned by the tool that queued the operation."
                                                              )]
                                                  string jobId,
                                                  [Description("Optional database profile name (use list_profiles to discover)."
                                                              )]
                                                  string? profile = null,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = repositoryFactory.GetBackgroundJobRepository(profile);
        var job = await jobRepo.GetAsync(jobId, ct);

        string result;
        if (job is null)
            result = $"No background job found with id '{jobId}'.";
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
                                   job.JobType,
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

    private static string? ComputeBoundaryHint(string jobType, object? parsedResult)
    {
        string? hint = null;
        if (jobType == BackgroundJobTypes.Rechunk && parsedResult is JsonElement root)
        {
            if (root.TryGetProperty(ResultJsonKeyBoundaryIssues, out var boundaryIssuesEl) &&
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
        }

        return hint;
    }

    [McpServerTool(Name = "list_jobs")]
    [Description("List recent background jobs (rechunk, rename_library, delete_version, delete_library, " +
                 "dryrun_scrape, index_project_dependencies, submit_url_correction), most recent first. " +
                 "Filter by jobType to narrow results. " +
                 "Use get_job_status(jobId) to poll a specific job. " +
                 "Does NOT list scrape_docs or rescrub_library jobs — " +
                 "use list_scrape_jobs and list_rescrub_jobs for those."
                )]
    public static async Task<string> ListJobs(RepositoryFactory repositoryFactory,
                                              [Description("Optional job type filter (e.g. 'rechunk', 'rename_library', 'delete_version', " +
                                                           "'delete_library', 'dryrun_scrape', 'index_project_dependencies', 'submit_url_correction'). " +
                                                           "Omit to list all types."
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

        var jobRepo = repositoryFactory.GetBackgroundJobRepository(profile);
        var jobs = await jobRepo.ListRecentAsync(jobType, limit, ct);

        var items = jobs.Select(j => new
                                         {
                                             j.Id,
                                             Status = j.Status.ToString(),
                                             j.JobType,
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

    private const string ResultJsonKeyBoundaryIssues = "BoundaryIssues";
    private const string ResultJsonKeyProcessed = "Processed";
    private const double PercentMultiplier = 100.0;
    private const double BoundaryThresholdHigh = 10.0;
    private const double BoundaryThresholdMedium = 5.0;
    private const string BoundaryHintRecommended = "rechunk_library recommended";
    private const string BoundaryHintMayHelp = "rechunk_library may help";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
