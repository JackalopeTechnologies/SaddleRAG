// IngestionTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Reconciliation;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for triggering ingestion and reloading vector indices
///     without restarting the server.
/// </summary>
[McpServerToolType]
public static class IngestionTools
{
    /// <summary>
    ///     Logger category marker for <see cref="IngestionTools" />. Lets the static
    ///     tool methods take <c>ILogger&lt;T&gt;</c> via DI — generic type parameters
    ///     can't point at a static class directly.
    /// </summary>
    public sealed class IngestionToolsLog
    {
    }

    [McpServerTool(Name = "dryrun_scrape")]
    [Description("Dry-run a documentation scrape — fetches every page with Playwright " +
                 "but does NOT store anything to the database or clone any GitHub repos. " +
                 "Returns { JobId, Status: 'Queued' } immediately; poll get_job_status for the " +
                 "full DryRunReport including page counts, crawl depth, and GitHub repos found. " +
                 "Use this BEFORE running scrape_docs on a new library to verify " +
                 "the URL patterns are correct and the crawl scope is reasonable. " +
                 "Pass seedUrls when the home page does not link to all sections (e.g., DocFX " +
                 "sites whose /api/ tree is reachable only through namespace index pages) — " +
                 "the seeds are added to the crawl queue alongside url, mirroring exactly " +
                 "what a real scrape_docs call with the same seedUrls would discover. " +
                 "NOTE: the historical `rootUrl` parameter has been renamed to `url` to align " +
                 "with scrape_docs/recon_library/add_page. `rootUrl` still works as a deprecated " +
                 "alias for one release and will be removed in the next."
                )]
    public static async Task<string> DryRunScrape(IngestionOrchestrator orchestrator,
                                                  [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                  IBackgroundJobRunner runner,
                                                  RepositoryFactory repositoryFactory,
                                                  IMonitorBroadcaster broadcaster,
                                                  ILogger<IngestionToolsLog> logger,
                                                  [Description("Library identifier — used to key the audit log for this dry run"
                                                              )]
                                                  string library,
                                                  [Description("Library version — used to key the audit log for this dry run"
                                                              )]
                                                  string version,
                                                  [Description("Root URL to begin crawling from")]
                                                  string? url = null,
                                                  [Description("DEPRECATED alias for `url`. Pass `url` instead. " +
                                                               "Will be removed in the next release; both names accepted in this one."
                                                              )]
                                                  string? rootUrl = null,
                                                  [Description("Allowed URL patterns (regex). Defaults to the url host.")]
                                                  string[]? allowedUrlPatterns = null,
                                                  [Description("Excluded URL patterns (regex)")]
                                                  string[]? excludedUrlPatterns = null,
                                                  [Description("Additional seed URLs added to the crawl queue alongside url. " +
                                                               "Same semantics as scrape_docs.seedUrls — use for sites where the home " +
                                                               "page does not link to every section that needs indexing."
                                                              )]
                                                  string[]? seedUrls = null,
                                                  [Description("Max pages to fetch in dry run, 0 = unlimited")]
                                                  int maxPages = DefaultDryRunMaxPages,
                                                  [Description("Delay between fetches in ms")]
                                                  int fetchDelayMs = 500,
                                                  [Description("Max depth for same-host pages outside the root path")]
                                                  int sameHostDepth = 5,
                                                  [Description("Max depth for pages on a different host entirely; 0 disables off-site crawling"
                                                              )]
                                                  int offSiteDepth = 1,
                                                  [Description("Optional database profile name")]
                                                  string? profile = null,
                                                  [Description("CSS selector that must resolve in the rendered DOM before " +
                                                               "content extraction. When set, bypasses the 3-page SPA " +
                                                               "auto-detection and forces SPA navigation immediately. " +
                                                               "Use for known SPA docs sites (e.g. '.mud-main-content' " +
                                                               "for MudBlazor)."
                                                              )]
                                                  string? waitForSelector = null,
                                                  [Description("Extra milliseconds to wait after NetworkIdle for slow-hydrating " +
                                                               "SPAs. Added on top of the built-in 300ms settle. Omit (null) " +
                                                               "for the default of no extra wait; pass 0 to explicitly disable. " +
                                                               "Only effective once the SPA navigator is active."
                                                              )]
                                                  int? spaWaitMs = null,
                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(logger);

        string? resolvedUrl = ParameterAliasReconciler.Resolve(url,
                                                               rootUrl,
                                                               ParamNameUrl,
                                                               ParamNameRootUrl,
                                                               logger);
        ArgumentException.ThrowIfNullOrEmpty(resolvedUrl);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (spaWaitMs is < 0)
            throw new ArgumentOutOfRangeException(nameof(spaWaitMs),
                                                  spaWaitMs,
                                                  "spaWaitMs must be non-negative");

        var allowed = allowedUrlPatterns ?? [new Uri(resolvedUrl).Host];

        var job = new ScrapeJob
                      {
                          RootUrl = resolvedUrl,
                          LibraryId = library,
                          Version = version,
                          LibraryHint = DryRunHint,
                          AllowedUrlPatterns = allowed,
                          ExcludedUrlPatterns = excludedUrlPatterns ?? [],
                          SeedUrls = seedUrls,
                          MaxPages = maxPages,
                          FetchDelayMs = fetchDelayMs,
                          SameHostDepth = sameHostDepth,
                          OffSiteDepth = offSiteDepth,
                          WaitForSelector = waitForSelector,
                          SpaWaitMs = spaWaitMs ?? 0
                      };

        var inputJson = JsonSerializer.Serialize(new
                                                     {
                                                         url = resolvedUrl,
                                                         library,
                                                         version,
                                                         maxPages,
                                                         fetchDelayMs,
                                                         sameHostDepth,
                                                         offSiteDepth
                                                     }
                                                );
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.DryRunScrape,
                                LibraryId = library,
                                Version = version,
                                InputJson = inputJson,
                                ItemsLabel = ItemsLabelPages
                            };

        var jobId = await runner.QueueAsync(jobRecord,
                                            async (record, onProgress, jobCt) =>
                                            {
                                                var auditRepo = repositoryFactory.GetScrapeAuditRepository(profile);
                                                await auditRepo.DeleteByLibraryVersionAsync(library, version, jobCt);
                                                var report = await orchestrator.DryRunAsync(job,
                                                                      library,
                                                                      version,
                                                                      record.Id,
                                                                      onProgress,
                                                                      jobCt
                                                                 );
                                                record.ResultJson = JsonSerializer.Serialize(report, smJsonOptions);
                                                broadcaster.BroadcastTick(record.Id);
                                            },
                                            ct
                                           );

        var response = new { JobId = jobId, Status = nameof(JobStatus.Queued) };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }


    [McpServerTool(Name = "get_scrape_status")]
    [Description("Check the status of a scrape job by its id. " +
                 "Status values: Queued (waiting), Running (in progress), " +
                 "Completed (fully indexed — call search_docs or get_class_reference), " +
                 "Failed (ingestion error — call get_server_logs to diagnose, then delete_version and retry), " +
                 "Cancelled (stopped by cancel_job — partial results kept; call delete_version to clear them). " +
                 "Poll at reasonable intervals (10–30s); the job id comes from scrape_docs or submit_url_correction."
                )]
    public static async Task<string> GetScrapeStatus(RepositoryFactory repositoryFactory,
                                                     [Description("Job id returned from scrape_docs or submit_url_correction")]
                                                     string jobId,
                                                     [Description("Optional database profile name")]
                                                     string? profile = null,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = repositoryFactory.GetJobRepository(profile);
        var job = await jobRepo.GetAsync(jobId, ct);

        string result;
        if (job is null || job.JobType != JobType.Scrape)
            result = $"No scrape job found with id '{jobId}'.";
        else
        {
            var progress = job.ScrapeProgress ?? new ScrapeProgress();
            var response = new
                               {
                                   job.Id,
                                   Status = job.Status.ToString(),
                                   job.PipelineState,
                                   progress.PagesQueued,
                                   progress.PagesFetched,
                                   progress.PagesClassified,
                                   progress.ChunksGenerated,
                                   progress.ChunksEmbedded,
                                   progress.ChunksCompleted,
                                   progress.PagesCompleted,
                                   job.ErrorCount,
                                   job.ErrorMessage,
                                   job.CreatedAt,
                                   job.StartedAt,
                                   job.CompletedAt,
                                   Library = job.LibraryId,
                                   job.Version
                               };

            result = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return result;
    }

    [McpServerTool(Name = "list_scrape_jobs")]
    [Description("List recent scrape jobs, most recent first. " +
                 "Use job ids from this list with get_scrape_status (poll progress) or cancel_job (stop a job). " +
                 "Running jobs with no recent progress (stale) appear in get_dashboard_index with a Stale flag — " +
                 "call cancel_job for them. Failed jobs: call get_server_logs to diagnose."
                )]
    public static async Task<string> ListScrapeJobs(RepositoryFactory repositoryFactory,
                                                    [Description("Maximum jobs to return (default 20)")]
                                                    int limit = 20,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var jobRepo = repositoryFactory.GetJobRepository(profile);
        var jobs = await jobRepo.ListRecentAsync(JobType.Scrape, limit, ct);

        var response = jobs.Select(j => new
                                            {
                                                j.Id,
                                                Status = j.Status.ToString(),
                                                j.PipelineState,
                                                Library = j.LibraryId,
                                                j.Version,
                                                j.CreatedAt,
                                                j.CompletedAt
                                            }
                                  );

        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    [McpServerTool(Name = "get_reextract_status")]
    [Description("Check the status of a reextract job by its id. " +
                 "Status values: Queued (waiting), Running (in progress — ChunksProcessed updates as chunks are examined), " +
                 "Completed (done — Result contains counts, diffs, and BoundaryHint), " +
                 "Failed (error — check ErrorMessage, then get_server_logs), " +
                 "Cancelled (stopped mid-run — partial index changes may have been applied). " +
                 "Poll at reasonable intervals (10–30s); the job id comes from reextract_library."
                )]
    public static async Task<string> GetRescrubStatus(RepositoryFactory repositoryFactory,
                                                      [Description("Job id returned from reextract_library")]
                                                      string jobId,
                                                      [Description("Optional database profile name")]
                                                      string? profile = null,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = repositoryFactory.GetJobRepository(profile);
        var job = await jobRepo.GetAsync(jobId, ct);

        string result;
        if (job is null || job.JobType != JobType.Rescrub)
            result = $"No reextract job found with id '{jobId}'.";
        else
        {
            var rescrubResult = ParseResult<RescrubResult>(job.ResultJson);
            var boundaryHint = rescrubResult is not null ? ResolveBoundaryHint(rescrubResult) : null;
            var response = new
                               {
                                   job.Id,
                                   Status = job.Status.ToString(),
                                   job.PipelineState,
                                   job.LibraryId,
                                   job.Version,
                                   ChunksTotal = job.ItemsTotal,
                                   ChunksProcessed = job.ItemsProcessed,
                                   ChunksChanged = rescrubResult?.Changed,
                                   job.ErrorMessage,
                                   job.CreatedAt,
                                   job.StartedAt,
                                   job.CompletedAt,
                                   job.LastProgressAt,
                                   BoundaryHint = boundaryHint,
                                   Result = rescrubResult
                               };

            result = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return result;
    }

    [McpServerTool(Name = "list_reextract_jobs")]
    [Description("List recent reextract jobs, most recent first. " +
                 "Use job ids from this list with get_reextract_status to poll progress. " +
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

        var jobRepo = repositoryFactory.GetJobRepository(profile);
        var jobs = await jobRepo.ListRecentAsync(JobType.Rescrub, limit, ct);

        var response = jobs.Select(j => new
                                            {
                                                j.Id,
                                                Status = j.Status.ToString(),
                                                j.PipelineState,
                                                j.LibraryId,
                                                j.Version,
                                                ChunksTotal = j.ItemsTotal,
                                                ChunksProcessed = j.ItemsProcessed,
                                                ChunksChanged = ParseResult<RescrubResult>(j.ResultJson)?.Changed,
                                                j.CreatedAt,
                                                j.CompletedAt
                                            }
                                  );

        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private static object? ResolveBoundaryHint(RescrubResult result)
    {
        var pct = result.Processed > 0 ? 100.0 * result.BoundaryIssues / result.Processed : 0.0;
        string? hint = pct switch
            {
                >= BoundaryHintRecommendThreshold => BoundaryHintRecommend,
                >= BoundaryHintMayHelpThreshold => BoundaryHintMayHelp,
                var _ => null
            };
        return new { pct, hint };
    }

    [McpServerTool(Name = "get_reembed_status")]
    [Description("Check the status of a reembed job by its id. " +
                 "Status values: Queued (waiting), Running (in progress — ChunksProcessed updates as chunks are re-embedded), " +
                 "Completed (done — Result contains counts plus the active and prior EmbeddingProviderId / ModelName / Dimensions), " +
                 "Failed (error — check ErrorMessage, then get_server_logs), " +
                 "Cancelled (stopped mid-run — partial chunk updates may have been written). " +
                 "Poll at reasonable intervals (10–30s); the job id comes from reembed_library."
                )]
    public static async Task<string> GetReembedStatus(RepositoryFactory repositoryFactory,
                                                      [Description("Job id returned from reembed_library")]
                                                      string jobId,
                                                      [Description("Optional database profile name")]
                                                      string? profile = null,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        var jobRepo = repositoryFactory.GetJobRepository(profile);
        var job = await jobRepo.GetAsync(jobId, ct);

        string result;
        if (job is null || job.JobType != JobType.Reembed)
            result = $"No reembed job found with id '{jobId}'.";
        else
        {
            var reembedResult = ParseResult<ReembedResult>(job.ResultJson);
            var response = new
                               {
                                   job.Id,
                                   Status = job.Status.ToString(),
                                   job.PipelineState,
                                   job.LibraryId,
                                   job.Version,
                                   ChunksTotal = job.ItemsTotal,
                                   ChunksProcessed = job.ItemsProcessed,
                                   job.ErrorMessage,
                                   job.CreatedAt,
                                   job.StartedAt,
                                   job.CompletedAt,
                                   job.LastProgressAt,
                                   Result = reembedResult
                               };

            result = JsonSerializer.Serialize(response, smJsonOptions);
        }

        return result;
    }

    [McpServerTool(Name = "list_reembed_jobs")]
    [Description("List recent reembed jobs, most recent first. " +
                 "Use job ids from this list with get_reembed_status to poll progress. " +
                 "Running jobs show ChunksProcessed / ChunksTotal for in-flight progress. " +
                 "Completed jobs include EmbeddingProviderId and EmbeddingModelName so you can confirm which provider was used."
                )]
    public static async Task<string> ListReembedJobs(RepositoryFactory repositoryFactory,
                                                     [Description("Maximum jobs to return (default 20)")]
                                                     int limit = 20,
                                                     [Description("Optional database profile name")]
                                                     string? profile = null,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);

        var jobRepo = repositoryFactory.GetJobRepository(profile);
        var jobs = await jobRepo.ListRecentAsync(JobType.Reembed, limit, ct);

        var response = jobs.Select(j => new
                                            {
                                                j.Id,
                                                Status = j.Status.ToString(),
                                                j.PipelineState,
                                                j.LibraryId,
                                                j.Version,
                                                ChunksTotal = j.ItemsTotal,
                                                ChunksProcessed = j.ItemsProcessed,
                                                j.CreatedAt,
                                                j.CompletedAt,
                                                EmbeddingProviderId = ParseResult<ReembedResult>(j.ResultJson)?.EmbeddingProviderId,
                                                EmbeddingModelName = ParseResult<ReembedResult>(j.ResultJson)?.EmbeddingModelName
                                            }
                                  );

        var json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    [McpServerTool(Name = "reload_profile")]
    [Description("Reload the in-memory vector index from MongoDB for a profile. " +
                 "Useful after manual data changes or to recover from index drift. " +
                 "Normally not needed — successful ingestion auto-reloads when it completes."
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

    private static T? ParseResult<T>(string? json) where T : class
    {
        T? result = null;
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                result = JsonSerializer.Deserialize<T>(json);
            }
            catch(JsonException)
            {
                // Malformed result payload — surface as null.
            }
        }
        return result;
    }

    private const double BoundaryHintMayHelpThreshold = 5.0;
    private const double BoundaryHintRecommendThreshold = 10.0;
    private const string BoundaryHintMayHelp = "rechunk_library may help";
    private const string BoundaryHintRecommend = "rechunk_library recommended";

    private const int DefaultDryRunMaxPages = 200;
    private const string DryRunHint = "Dry run";
    private const string ItemsLabelPages = "pages";

    private const string ParamNameUrl = "url";
    private const string ParamNameRootUrl = "rootUrl";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
