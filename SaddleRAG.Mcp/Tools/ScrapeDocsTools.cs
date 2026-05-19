// ScrapeDocsTools.cs
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
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
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
                 "fans out from multiple entry points. resume=true reuses the most recent ScrapeJob's rootUrl, patterns, " +
                 "and seedUrls when url is omitted. If the library is flagged URL_SUSPECT, resume=true returns Status=Refused — " +
                 "call submit_url_correction(library, version, newUrl) first to clear the flag and re-queue with a corrected URL."
                )]
    public static async Task<string> ScrapeDocs(ScrapeJobRunner runner,
                                                RepositoryFactory repositoryFactory,
                                                [Description("Root URL of the documentation site (optional when resume=true)"
                                                            )]
                                                string? url = null,
                                                [Description("Unique library identifier for cache key")]
                                                string libraryId = "",
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
                                                string[]? allowedUrlPatterns = null,
                                                [Description("Optional URL patterns (regex) to exclude.")]
                                                string[]? excludedUrlPatterns = null,
                                                [Description("Additional HTTP status codes to treat as rate-limit signals, on top of the built-in defaults (429, 503). " +
                                                             "Use for site-specific soft-limit responses: [502] for Infragistics and similar CDNs, " +
                                                             "[520, 521, 522] for Cloudflare rate walls.")]
                                                int[]? additionalRateLimitStatusCodes = null,
                                                [Description("Additional seed URLs added to the crawl queue alongside the root. Use for sites where " +
                                                             "the home page does not link to every section that needs indexing — e.g., on DocFX-style " +
                                                             "sites, pass [\"https://site/api/MyLib/index.htm\"] so the /api/ tree is reachable. Each seed " +
                                                             "is filtered through allowedUrlPatterns just like links discovered during the crawl, so " +
                                                             "out-of-scope seeds are dropped at the audit boundary."
                                                            )]
                                                string[]? seedUrls = null,
                                                [Description("Resume the most recent scrape for this (libraryId, version), reusing its RootUrl/patterns/seedUrls"
                                                            )]
                                                bool resume = false,
                                                [Description("Optional database profile name")]
                                                string? profile = null,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

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
            var previous = recent.Where(j => j.LibraryId == libraryId && j.Version == version)
                                 .OrderByDescending(j => j.CreatedAt)
                                 .FirstOrDefault();
            var previousJob = DeserializeScrapeJob(previous);

            if (previousJob is null)
            {
                var noPrior = new
                                  {
                                      Status = StatusNoPriorJob,
                                      Message =
                                          $"resume=true but no previous scrape job exists for {libraryId} v{version}. Pass url to start a fresh scrape."
                                  };
                json = JsonSerializer.Serialize(noPrior, new JsonSerializerOptions { WriteIndented = true });
                earlyResponseEmitted = true;
            }
            else
            {
                var versionRecord = await libraryRepo.GetVersionAsync(libraryId, version, ct);
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
                                         LibraryId = libraryId,
                                         Version = version,
                                         LibraryHint = hint ?? previousJob.LibraryHint,
                                         AllowedUrlPatterns = allowedUrlPatterns ?? previousJob.AllowedUrlPatterns,
                                         ExcludedUrlPatterns = excludedUrlPatterns ?? previousJob.ExcludedUrlPatterns,
                                         SeedUrls = seedUrls ?? previousJob.SeedUrls,
                                         MaxPages = maxPages,
                                         FetchDelayMs = fetchDelayMs,
                                         ForceClean = force,
                                         AdditionalRateLimitStatusCodes = additionalRateLimitStatusCodes ?? previousJob.AdditionalRateLimitStatusCodes
                                     };
                }
            }
        }

        if (!earlyResponseEmitted)
        {
            var existingVersion = await libraryRepo.GetVersionAsync(libraryId, version, ct);
            if (existingVersion != null && !force)
            {
                var cached = new
                                 {
                                     Status = StatusAlreadyCached,
                                     LibraryId = libraryId,
                                     Version = version,
                                     Message = $"Documentation for {libraryId} v{version} is already indexed " +
                                               $"({existingVersion.ChunkCount} chunks). Use force=true to re-scrape."
                                 };
                json = JsonSerializer.Serialize(cached, new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                string resolvedUrl = url ?? string.Empty;
                jobToQueue ??= BuildJobForUrl(resolvedUrl,
                                              libraryId,
                                              version,
                                              hint,
                                              maxPages,
                                              fetchDelayMs,
                                              force,
                                              allowedUrlPatterns,
                                              excludedUrlPatterns,
                                              additionalRateLimitStatusCodes,
                                              seedUrls
                                             );
                var scrapeAuditRepo = repositoryFactory.GetScrapeAuditRepository(profile);
                await scrapeAuditRepo.DeleteByLibraryVersionAsync(libraryId, version, ct);
                var jobId = await runner.QueueAsync(jobToQueue, profile, ct);
                var response = new
                                   {
                                       JobId = jobId,
                                       Status = nameof(ScrapeJobStatus.Queued),
                                       LibraryId = libraryId,
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
                                            string[]? seedUrls)
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
                          SeedUrls = seedUrls
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

    private static readonly JsonSerializerOptions smIndexOptions = new JsonSerializerOptions { WriteIndented = true };
}
