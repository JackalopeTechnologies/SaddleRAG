// ScrapeDocsTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

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
using SaddleRAG.Ingestion.Scanning;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for on-demand documentation scraping and
///     project dependency indexing.
/// </summary>
[McpServerToolType]
public static class ScrapeDocsTools
{
    /// <summary>
    ///     Logger category marker for <see cref="ScrapeDocsTools" />. Lets the static
    ///     tool methods take <c>ILogger&lt;T&gt;</c> via DI — generic type parameters
    ///     can't point at a static class directly.
    /// </summary>
    public sealed class ScrapeDocsToolsLog
    {
    }

    /// <summary>
    ///     Fetch documentation from the source site and run the full ingest pipeline.
    ///     Supports cache-aware refresh and resuming prior scrapes by reusing stored job configuration.
    /// </summary>
    [McpServerTool(Name = "scrape_docs")]
    [Description("Fetch documentation from a URL and run the full ingest pipeline against the live source site: " +
                 "crawl, classify, chunk, embed, and persist the refreshed indexable content. Cache-aware: returns " +
                 "AlreadyCached unless force=true. Use this for first-time ingest, or when you need to override the " +
                 "URL or crawl patterns. For routine refreshes of an already-scraped library, prefer rescrape_library " +
                 "(library + version only — config is pulled from the most recent scrape job and stored pages seed " +
                 "the crawl). Pass allowedUrlPatterns / excludedUrlPatterns only if the auto-derived host filter is too " +
                 "narrow or too broad. Pass seedUrls when the home page does not link to all sections that need indexing " +
                 "(e.g., DocFX-generated sites where the /api/ tree is reachable only through namespace index pages, not " +
                 "from the nav bar) — each seed URL is added to the crawl queue alongside the root, so a single scrape " +
                 "fans out from multiple entry points. resume=true reuses the most recent ScrapeJob's url, patterns, " +
                 "and seedUrls when url is omitted. If the library is flagged URL_SUSPECT, resume=true returns Status=Refused — " +
                 "call submit_url_correction(library, version, newUrl) first to clear the flag and re-queue with a corrected URL. " +
                 "NOTE: the historical `libraryId` parameter has been renamed to `library` to align with " +
                 "every other MCP tool. `libraryId` still works as a deprecated alias for one release and " +
                 "will be removed in the next."
                )]
    public static async Task<string> ScrapeDocs(ScrapeJobRunner runner,
                                                RepositoryFactory repositoryFactory,
                                                ILogger<ScrapeDocsToolsLog> logger,
                                                [Description("Root URL of the documentation site (optional when resume=true)"
                                                            )]
                                                string? url = null,
                                                [Description("Library identifier for cache key")]
                                                string library = "",
                                                [Description("DEPRECATED alias for `library`. Pass `library` instead. " +
                                                             "Will be removed in the next release; both names accepted in this one."
                                                            )]
                                                string? libraryId = null,
                                                [Description("Version string for cache key")]
                                                string version = "",
                                                [Description("Human-readable hint about what this library is")]
                                                string? hint = null,
                                                [Description("Maximum pages to crawl (0 = unlimited, default)")]
                                                int maxPages = DefaultMaxPages,
                                                [Description("Delay between fetches in ms (default 500)")]
                                                int fetchDelayMs = 500,
                                                [Description("Re-scrape even if already cached")]
                                                bool force = false,
                                                [Description("Optional URL patterns (regex) to allow. Defaults to the rootUrl host when omitted."
                                                            )]
                                                JsonElement? allowedUrlPatterns = null,
                                                [Description("Optional URL patterns (regex) to exclude.")]
                                                JsonElement? excludedUrlPatterns = null,
                                                [Description("Additional HTTP status codes to treat as rate-limit signals, on top of the built-in defaults (429, 503). " +
                                                             "Use for site-specific soft-limit responses: [502] for Infragistics and similar CDNs, " +
                                                             "[520, 521, 522] for Cloudflare rate walls.")]
                                                JsonElement? additionalRateLimitStatusCodes = null,
                                                [Description("Additional seed URLs added to the crawl queue alongside the root. Use for sites where " +
                                                             "the home page does not link to every section that needs indexing — e.g., on DocFX-style " +
                                                             "sites, pass [\"https://site/api/MyLib/index.htm\"] so the /api/ tree is reachable. Each seed " +
                                                             "is filtered through allowedUrlPatterns just like links discovered during the crawl, so " +
                                                             "out-of-scope seeds are dropped at the audit boundary."
                                                            )]
                                                JsonElement? seedUrls = null,
                                                [Description("Resume the most recent scrape for this (library, version), reusing its url/patterns/seedUrls"
                                                            )]
                                                bool resume = false,
                                                [Description("Optional database profile name")]
                                                string? profile = null,
                                                [Description("CSS selector that must resolve in the rendered DOM before " +
                                                             "content extraction. When set, bypasses the 3-page SPA " +
                                                             "auto-detection and forces SPA navigation immediately. " +
                                                             "Use for known SPA docs sites (e.g. '.mud-main-content' " +
                                                             "for MudBlazor). Carried forward on resume."
                                                            )]
                                                string? waitForSelector = null,
                                                [Description("Extra milliseconds to wait after NetworkIdle for slow-hydrating " +
                                                             "SPAs. Added on top of the built-in 300ms settle. Omit (null) " +
                                                             "on resume to carry forward the previous job's value; pass 0 to " +
                                                             "explicitly disable; pass a positive value to override."
                                                            )]
                                                int? spaWaitMs = null,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(logger);

        string resolvedLibrary = ParameterAliasReconciler.Resolve(library,
                                                                  libraryId,
                                                                  ParamNameLibrary,
                                                                  ParamNameLibraryId,
                                                                  logger) ?? string.Empty;
        ArgumentException.ThrowIfNullOrEmpty(resolvedLibrary);
        ArgumentException.ThrowIfNullOrEmpty(version);
        if (spaWaitMs is < 0)
            throw new ArgumentOutOfRangeException(nameof(spaWaitMs),
                                                  spaWaitMs,
                                                  "spaWaitMs must be non-negative");

        string[]? parsedAllowedUrlPatterns = McpStringArrayArgumentParser.Parse(allowedUrlPatterns,
                                                                                  nameof(allowedUrlPatterns)
                                                                                 );
        string[]? parsedExcludedUrlPatterns = McpStringArrayArgumentParser.Parse(excludedUrlPatterns,
                                                                                   nameof(excludedUrlPatterns)
                                                                                  );
        string[]? parsedSeedUrls = McpStringArrayArgumentParser.Parse(seedUrls, nameof(seedUrls));
        int[]? parsedAdditionalRateLimitStatusCodes = McpIntegerArrayArgumentParser.Parse(additionalRateLimitStatusCodes,
                                                     nameof(additionalRateLimitStatusCodes)
                                                    );

        if (!resume && string.IsNullOrEmpty(url))
            throw new ArgumentException("url is required when resume=false");

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        var json = string.Empty;
        ScrapeJob? jobToQueue = null;
        var earlyResponseEmitted = false;

        if (resume)
        {
            var jobRepo = repositoryFactory.GetJobRepository(profile);
            var recent = await jobRepo.ListRecentAsync(JobType.Scrape, limit: 100, ct);
            var previous = recent.Where(j => j.LibraryId == resolvedLibrary && j.Version == version)
                                 .OrderByDescending(j => j.CreatedAt)
                                 .FirstOrDefault();
            var previousJob = DeserializeScrapeJob(previous);

            if (previousJob is null)
            {
                var noPrior = new
                                  {
                                      Status = StatusNoPriorJob,
                                      Message =
                                          $"resume=true but no previous scrape job exists for {resolvedLibrary} v{version}. Pass url to start a fresh scrape."
                                  };
                json = JsonSerializer.Serialize(noPrior, new JsonSerializerOptions { WriteIndented = true });
                earlyResponseEmitted = true;
            }
            else
            {
                var versionRecord = await libraryRepo.GetVersionAsync(resolvedLibrary, version, ct);
                if (versionRecord is { Suspect: true })
                {
                    var refused = new
                                      {
                                          Status = StatusRefused,
                                          Reason = ReasonUrlSuspect,
                                          versionRecord.SuspectReasons,
                                          Hint =
                                              "Call submit_url_correction(library, version, newUrl) with a corrected URL."
                                      };
                    json = JsonSerializer.Serialize(refused, new JsonSerializerOptions { WriteIndented = true });
                    earlyResponseEmitted = true;
                }
                else
                {
                    jobToQueue = new ScrapeJob
                                     {
                                         RootUrl = url ?? previousJob.RootUrl,
                                         LibraryId = resolvedLibrary,
                                         Version = version,
                                         LibraryHint = hint ?? previousJob.LibraryHint,
                                         AllowedUrlPatterns = parsedAllowedUrlPatterns ?? previousJob.AllowedUrlPatterns,
                                         ExcludedUrlPatterns = parsedExcludedUrlPatterns ?? previousJob.ExcludedUrlPatterns,
                                         SeedUrls = parsedSeedUrls ?? previousJob.SeedUrls,
                                         MaxPages = maxPages,
                                         FetchDelayMs = fetchDelayMs,
                                         ForceClean = force,
                                         AdditionalRateLimitStatusCodes = parsedAdditionalRateLimitStatusCodes ?? previousJob.AdditionalRateLimitStatusCodes,
                                         WaitForSelector = waitForSelector ?? previousJob.WaitForSelector,
                                         SpaWaitMs = spaWaitMs ?? previousJob.SpaWaitMs
                                     };
                }
            }
        }

        if (!earlyResponseEmitted)
        {
            var existingVersion = await libraryRepo.GetVersionAsync(resolvedLibrary, version, ct);
            if (existingVersion != null && !force)
            {
                var cached = new
                                 {
                                     Status = StatusAlreadyCached,
                                     LibraryId = resolvedLibrary,
                                     Version = version,
                                     Message = $"Documentation for {resolvedLibrary} v{version} is already indexed " +
                                               $"({existingVersion.ChunkCount} chunks). Use force=true to re-scrape."
                                 };
                json = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                string resolvedUrl = url ?? string.Empty;
                jobToQueue ??= BuildJobForUrl(resolvedUrl,
                                              resolvedLibrary,
                                              version,
                                              hint,
                                              maxPages,
                                              fetchDelayMs,
                                              force,
                                              parsedAllowedUrlPatterns,
                                              parsedExcludedUrlPatterns,
                                              parsedAdditionalRateLimitStatusCodes,
                                              parsedSeedUrls,
                                              waitForSelector,
                                              spaWaitMs ?? 0
                                             );
                var scrapeAuditRepo = repositoryFactory.GetScrapeAuditRepository(profile);
                await scrapeAuditRepo.DeleteByLibraryVersionAsync(resolvedLibrary, version, ct);
                var jobId = await runner.QueueAsync(jobToQueue, profile, ct);
                var response = new
                                   {
                                       JobId = jobId,
                                       Status = nameof(ScrapeJobStatus.Queued),
                                       LibraryId = resolvedLibrary,
                                       Version = version,
                                       Message =
                                           $"Scrape job queued. Poll get_scrape_status with jobId='{jobId}' for progress."
                                   };
                json = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
            }
        }

        return json;
    }

    private static ScrapeJob? DeserializeScrapeJob(JobRecord? record)
    {
        ScrapeJob? result = null;
        if (!string.IsNullOrEmpty(record?.InputJson))
        {
            try
            {
                result = JsonSerializer.Deserialize<ScrapeJob>(record.InputJson);
            }
            catch(JsonException)
            {
                // Malformed input — treat as no prior job.
            }
        }

        return result;
    }

    private static ScrapeJob BuildJobForUrl(string url,
                                            string libraryId,
                                            string version,
                                            string? hint,
                                            int maxPages,
                                            int fetchDelayMs,
                                            bool force,
                                            string[]? allowedUrlPatterns,
                                            string[]? excludedUrlPatterns,
                                            int[]? additionalRateLimitStatusCodes,
                                            string[]? seedUrls,
                                            string? waitForSelector,
                                            int spaWaitMs)
    {
        ScrapeJob job;
        if (allowedUrlPatterns != null || excludedUrlPatterns != null)
        {
            job = new ScrapeJob
                      {
                          RootUrl = url,
                          LibraryId = libraryId,
                          Version = version,
                          LibraryHint = hint ?? string.Empty,
                          AllowedUrlPatterns = allowedUrlPatterns ?? [new Uri(url).Host],
                          ExcludedUrlPatterns = excludedUrlPatterns ?? [],
                          MaxPages = maxPages,
                          FetchDelayMs = fetchDelayMs,
                          ForceClean = force,
                          AdditionalRateLimitStatusCodes = additionalRateLimitStatusCodes,
                          SeedUrls = seedUrls,
                          WaitForSelector = waitForSelector,
                          SpaWaitMs = spaWaitMs
                      };
        }
        else
        {
            job = ScrapeJobFactory.CreateFromUrl(url,
                                                 libraryId,
                                                 version,
                                                 hint,
                                                 maxPages,
                                                 fetchDelayMs,
                                                 force,
                                                 additionalRateLimitStatusCodes
                                                );
            if (seedUrls is { Length: > 0 })
                job = job with { SeedUrls = seedUrls };
            if (!string.IsNullOrEmpty(waitForSelector) || spaWaitMs > 0)
                job = job with { WaitForSelector = waitForSelector, SpaWaitMs = spaWaitMs };
        }

        return job;
    }


    /// <summary>
    ///     Scan a project to discover all package dependencies and scrape their docs.
    /// </summary>
    [McpServerTool(Name = "index_project_dependencies")]
    [Description("Scan a project to discover all package dependencies (NuGet, npm, pip), " +
                 "resolve their documentation URLs, and scrape everything not already cached. " +
                 "Pass a directory path to auto-detect project files, or a specific " +
                 ".sln/.csproj/package.json/requirements.txt/pyproject.toml file. " +
                 "Returns { JobId, Status: 'Queued' } immediately; poll get_job_status for the " +
                 "full report showing what was found, cached, queued, and unresolved."
                )]
    public static async Task<string> IndexProjectDependencies(DependencyIndexer indexer,
                                                              [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                              IBackgroundJobRunner runner,
                                                              [Description("Project root directory or specific project file path"
                                                                          )]
                                                              string path,
                                                              [Description("Optional database profile name")]
                                                              string? profile = null,
                                                              CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var inputJson = JsonSerializer.Serialize(new { path, profile });
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.IndexProjectDependencies,
                                Profile = profile,
                                InputJson = inputJson,
                                ItemsLabel = ItemsLabelPackages
                            };

        var jobId = await runner.QueueAsync(jobRecord,
                                            async (record, onProgress, jobCt) =>
                                            {
                                                var report =
                                                    await indexer.IndexProjectAsync(path, profile, onProgress, jobCt);
                                                record.ResultJson = JsonSerializer.Serialize(report, smIndexOptions);
                                            },
                                            ct
                                           );

        var response = new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) };
        return JsonSerializer.Serialize(response, smIndexOptions);
    }

    private const string StatusAlreadyCached = "AlreadyCached";
    private const string StatusNoPriorJob = "NoPriorJob";
    private const string StatusRefused = "Refused";
    private const string ReasonUrlSuspect = "URL_SUSPECT";
    private const string ItemsLabelPackages = "packages";
    private const int DefaultMaxPages = 0;

    private const string ParamNameLibrary = "library";
    private const string ParamNameLibraryId = "libraryId";

    private static readonly JsonSerializerOptions smIndexOptions = new JsonSerializerOptions { WriteIndented = true };
}
