// IngestionTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Crawling;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for triggering ingestion and reloading vector indices
///     without restarting the server.
/// </summary>
[McpServerToolType]
public static class IngestionTools
{
    [McpServerTool(Name = "dryrun_scrape")]
    [Description("Dry-run a documentation scrape — fetches every page with Playwright " +
                 "but does NOT store anything to the database or clone any GitHub repos. " +
                 "Returns { JobId, Status: 'Queued' } immediately; poll get_job_status for the " +
                 "full DryRunReport including page counts, crawl depth, and GitHub repos found. " +
                 "Use this BEFORE running scrape_library on a new library to verify " +
                 "the URL patterns are correct and the crawl scope is reasonable."
                )]
    public static async Task<string> DryRunScrape(PageCrawler crawler,
                                                  IBackgroundJobRunner runner,
                                                  [Description("Root URL to begin crawling from")]
                                                  string rootUrl,
                                                  [Description("Allowed URL patterns (regex). Defaults to the rootUrl host."
                                                              )]
                                                  string[]? allowedUrlPatterns = null,
                                                  [Description("Excluded URL patterns (regex)")]
                                                  string[]? excludedUrlPatterns = null,
                                                  [Description("Max pages to fetch in dry run, 0 = unlimited")]
                                                  int maxPages = DefaultDryRunMaxPages,
                                                  [Description("Delay between fetches in ms")]
                                                  int fetchDelayMs = 500,
                                                  [Description("Max depth for same-host pages outside the root path")]
                                                  int sameHostDepth = 5,
                                                  [Description("Max depth for pages on a different host entirely; 0 disables off-site crawling"
                                                              )]
                                                  int offSiteDepth = 1,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(crawler);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(rootUrl);

        var allowed = allowedUrlPatterns ?? [new Uri(rootUrl).Host];

        var job = new ScrapeJob
                      {
                          RootUrl = rootUrl,
                          LibraryId = DryRunLibraryId,
                          Version = DryRunVersion,
                          LibraryHint = DryRunHint,
                          AllowedUrlPatterns = allowed,
                          ExcludedUrlPatterns = excludedUrlPatterns ?? [],
                          MaxPages = maxPages,
                          FetchDelayMs = fetchDelayMs,
                          SameHostDepth = sameHostDepth,
                          OffSiteDepth = offSiteDepth
                      };

        var inputJson = JsonSerializer.Serialize(new { rootUrl, maxPages, fetchDelayMs, sameHostDepth, offSiteDepth });
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.DryRunScrape,
                                InputJson = inputJson,
                                ItemsLabel = ItemsLabelPages
                            };

        var jobId = await runner.QueueAsync(jobRecord,
                                            async (record, onProgress, jobCt) =>
                                            {
                                                var report = await crawler.DryRunAsync(job, onProgress, jobCt);
                                                record.ResultJson = JsonSerializer.Serialize(report, smJsonOptions);
                                            },
                                            ct);

        var response = new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }


    [McpServerTool(Name = "get_scrape_status")]
    [Description("Check the status of a scrape job by its id. " +
                 "Status values: Queued (waiting), Running (in progress), " +
                 "Completed (fully indexed — call search_docs or get_class_reference), " +
                 "Failed (ingestion error — call get_server_logs to diagnose, then delete_version and retry), " +
                 "Cancelled (stopped by cancel_scrape — partial results kept; call delete_version to clear them). " +
                 "Poll at reasonable intervals (10–30s); the job id comes from scrape_docs or submit_url_correction."
                )]
    public static async Task<string> GetScrapeStatus(RepositoryFactory repositoryFactory,
                                                     [Description("Job id returned from scrape_library")]
                                                     string jobId,
                                                     [Description("Optional database profile name")]
                                                     string? profile = null,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
        var job = await jobRepo.GetAsync(jobId, ct);

        string result;
        if (job == null)
            result = $"No scrape job found with id '{jobId}'.";
        else
        {
            var response = new
                               {
                                   job.Id,
                                   job.Status,
                                   job.PipelineState,
                                   job.PagesQueued,
                                   job.PagesFetched,
                                   job.PagesClassified,
                                   job.ChunksGenerated,
                                   job.ChunksEmbedded,
                                   job.ChunksCompleted,
                                   job.PagesCompleted,
                                   job.ErrorCount,
                                   job.ErrorMessage,
                                   job.CreatedAt,
                                   job.StartedAt,
                                   job.CompletedAt,
                                   Library = job.Job.LibraryId,
                                   job.Job.Version
                               };

            result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }

        return result;
    }

    [McpServerTool(Name = "list_scrape_jobs")]
    [Description("List recent scrape jobs, most recent first. " +
                 "Use job ids from this list with get_scrape_status (poll progress) or cancel_scrape (stop a job). " +
                 "Running jobs with no recent progress (stale) appear in get_dashboard_index with a Stale flag — " +
                 "call cancel_scrape for them. Failed jobs: call get_server_logs to diagnose."
                )]
    public static async Task<string> ListScrapeJobs(RepositoryFactory repositoryFactory,
                                                    [Description("Maximum jobs to return (default 20)")]
                                                    int limit = 20,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var jobRepo = repositoryFactory.GetScrapeJobRepository(profile);
        var jobs = await jobRepo.ListRecentAsync(limit, ct);

        var response = jobs.Select(j => new
                                            {
                                                j.Id,
                                                j.Status,
                                                j.PipelineState,
                                                Library = j.Job.LibraryId,
                                                j.Job.Version,
                                                j.CreatedAt,
                                                j.CompletedAt
                                            }
                                  );

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        return json;
    }

    [McpServerTool(Name = "get_rescrub_status")]
    [Description("Check the status of a rescrub job by its id. " +
                 "Status values: Queued (waiting), Running (in progress — ChunksProcessed updates as chunks are examined), " +
                 "Completed (done — Result contains counts, diffs, and BoundaryHint), " +
                 "Failed (error — check ErrorMessage, then get_server_logs), " +
                 "Cancelled (stopped mid-run — partial index changes may have been applied). " +
                 "Poll at reasonable intervals (10–30s); the job id comes from rescrub_library."
                )]
    public static async Task<string> GetRescrubStatus(RepositoryFactory repositoryFactory,
                                                       [Description("Job id returned from rescrub_library")]
                                                       string jobId,
                                                       [Description("Optional database profile name")]
                                                       string? profile = null,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = repositoryFactory.GetRescrubJobRepository(profile);
        var job = await jobRepo.GetAsync(jobId, ct);

        string result;
        if (job == null)
            result = $"No rescrub job found with id '{jobId}'.";
        else
        {
            var boundaryHint = job.Result != null ? ResolveBoundaryHint(job.Result) : null;
            var response = new
                               {
                                   job.Id,
                                   job.Status,
                                   job.PipelineState,
                                   job.LibraryId,
                                   job.Version,
                                   job.ChunksTotal,
                                   job.ChunksProcessed,
                                   job.ChunksChanged,
                                   job.ErrorMessage,
                                   job.CreatedAt,
                                   job.StartedAt,
                                   job.CompletedAt,
                                   job.LastProgressAt,
                                   BoundaryHint = boundaryHint,
                                   Result = job.Result
                               };

            result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }

        return result;
    }

    [McpServerTool(Name = "list_rescrub_jobs")]
    [Description("List recent rescrub jobs, most recent first. " +
                 "Use job ids from this list with get_rescrub_status to poll progress. " +
                 "Running jobs show ChunksProcessed / ChunksTotal for in-flight progress. " +
                 "Completed jobs include a BoundaryHint to act on before calling search_docs."
                )]
    public static async Task<string> ListRescrubJobs(RepositoryFactory repositoryFactory,
                                                      [Description("Maximum jobs to return (default 20)")]
                                                      int limit = 20,
                                                      [Description("Optional database profile name")]
                                                      string? profile = null,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var jobRepo = repositoryFactory.GetRescrubJobRepository(profile);
        var jobs = await jobRepo.ListRecentAsync(limit, ct);

        var response = jobs.Select(j => new
                                            {
                                                j.Id,
                                                j.Status,
                                                j.PipelineState,
                                                j.LibraryId,
                                                j.Version,
                                                j.ChunksTotal,
                                                j.ChunksProcessed,
                                                j.ChunksChanged,
                                                j.CreatedAt,
                                                j.CompletedAt
                                            }
                                  );

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        return json;
    }

    private static object? ResolveBoundaryHint(SaddleRAG.Core.Models.RescrubResult result)
    {
        var pct = result.Processed > 0 ? 100.0 * result.BoundaryIssues / result.Processed : 0.0;
        string? hint = pct switch
        {
            >= BoundaryHintRecommendThreshold => BoundaryHintRecommend,
            >= BoundaryHintMayHelpThreshold => BoundaryHintMayHelp,
            _ => null
        };
        return new { pct, hint };
    }

    private const double BoundaryHintMayHelpThreshold = 5.0;
    private const double BoundaryHintRecommendThreshold = 10.0;
    private const string BoundaryHintMayHelp = "rechunk_library may help";
    private const string BoundaryHintRecommend = "rechunk_library recommended";

    [McpServerTool(Name = "reload_profile")]
    [Description("Reload the in-memory vector index from MongoDB for a profile. " +
                 "Useful after manual data changes or to recover from index drift. " +
                 "Normally not needed — scrape_library auto-reloads when ingestion completes."
                )]
    public static async Task<string> ReloadProfile(ScrapeJobRunner runner,
                                                   [Description("Optional database profile name")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);

        await runner.ReloadProfileAsync(profile, ct);
        return $"Reloaded vector index for profile '{profile ?? "(default)"}'.";
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const int DefaultDryRunMaxPages = 200;
    private const string DryRunLibraryId = "dryrun";
    private const string DryRunVersion = "dryrun";
    private const string DryRunHint = "Dry run";
    private const string ItemsLabelPages = "pages";
}
