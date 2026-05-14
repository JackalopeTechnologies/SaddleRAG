// RescrapeTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool exposing rescrape_library — the canonical "refresh an
///     already-scraped library from its source site" entry point. Pulls
///     crawl configuration (RootUrl, AllowedUrlPatterns, ExcludedUrlPatterns,
///     LibraryHint) from the most recent ScrapeJob for (library, version),
///     and seeds the crawl queue from every stored PageRecord URL so dead
///     pages are surfaced as orphans, unchanged pages are picked up by the
///     normal pipeline, and newly added pages get discovered through
///     link-following. Compare scrape_docs, which is for first-time ingest
///     or when an explicit URL/pattern override is needed.
/// </summary>
[McpServerToolType]
public static class RescrapeTools
{
    [McpServerTool(Name = "rescrape_library")]
    [Description("Re-scrape an existing library from its source site without needing to remember the original URL or " +
                 "crawl patterns. Takes library + version only; the tool looks up the most recent scrape job for " +
                 "those keys, reuses its RootUrl / AllowedUrlPatterns / ExcludedUrlPatterns / LibraryHint, and seeds " +
                 "the crawl queue from every stored page URL so the pipeline re-fetches everything you already have " +
                 "(dead pages become orphans, unchanged pages are picked up, new pages are discovered through normal " +
                 "link-following). Use this for routine refreshes of an already-indexed library. For first-time ingest " +
                 "of a brand-new library, or when you need to override URL or pattern configuration, use scrape_docs " +
                 "instead. Returns { JobId, Status: 'Queued' } immediately — poll get_scrape_status(jobId) for " +
                 "progress; rescrape jobs land in the same scrapeJobs collection as scrape_docs jobs. Returns " +
                 "Status='NoPriorJob' when (library, version) has no scrape job on record."
                )]
    public static async Task<string> RescrapeLibrary(ScrapeJobRunner runner,
                                                     RepositoryFactory repositoryFactory,
                                                     [Description("Library identifier (e.g. 'aerotech-aeroscript')")]
                                                     string library,
                                                     [Description("Library version (e.g. '2025.3')")]
                                                     string version,
                                                     [Description("Maximum pages to crawl (0 = unlimited, default)")]
                                                     int maxPages = DefaultMaxPages,
                                                     [Description("Delay between fetches in ms (default 500)")]
                                                     int fetchDelayMs = DefaultFetchDelayMs,
                                                     [Description("If true, clear existing chunks before the rescrape starts. " +
                                                                  "Default false — the index stage replaces chunks naturally as new content arrives."
                                                                 )]
                                                     bool force = false,
                                                     [Description("Optional database profile name")]
                                                     string? profile = null,
                                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var jobRepo = repositoryFactory.GetJobRepository(profile);
        var recent = await jobRepo.ListRecentAsync(JobType.Scrape, RecentJobScanLimit, ct);
        var previous = recent.Where(j => j.LibraryId == library && j.Version == version)
                             .OrderByDescending(j => j.CreatedAt)
                             .FirstOrDefault();
        var previousJob = DeserializeScrapeJob(previous);

        string json;
        if (previousJob is null)
        {
            var noPrior = new
                              {
                                  Status = StatusNoPriorJob,
                                  Message = $"No prior scrape job found for {library} v{version}. " +
                                            $"Call scrape_docs(url, library='{library}', version='{version}') first."
                              };
            json = JsonSerializer.Serialize(noPrior, smJsonOptions);
        }
        else
        {
            var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
            var versionRecord = await libraryRepo.GetVersionAsync(library, version, ct);
            if (versionRecord is { Suspect: true })
            {
                var refused = new
                                  {
                                      Status = StatusRefused,
                                      Reason = ReasonUrlSuspect,
                                      versionRecord.SuspectReasons,
                                      Hint = "Call submit_url_correction(library, version, newUrl) with a corrected URL."
                                  };
                json = JsonSerializer.Serialize(refused, smJsonOptions);
            }
            else
            {
                var job = new ScrapeJob
                              {
                                  RootUrl = previousJob.RootUrl,
                                  LibraryId = library,
                                  Version = version,
                                  LibraryHint = previousJob.LibraryHint,
                                  AllowedUrlPatterns = previousJob.AllowedUrlPatterns,
                                  ExcludedUrlPatterns = previousJob.ExcludedUrlPatterns,
                                  MaxPages = maxPages,
                                  FetchDelayMs = fetchDelayMs,
                                  ForceClean = force,
                                  SeedFromStoredPages = true
                              };

                var scrapeAuditRepo = repositoryFactory.GetScrapeAuditRepository(profile);
                await scrapeAuditRepo.DeleteByLibraryVersionAsync(library, version, ct);
                var jobId = await runner.QueueAsync(job, profile, ct);

                var response = new
                                   {
                                       JobId = jobId,
                                       Status = nameof(JobStatus.Queued),
                                       LibraryId = library,
                                       Version = version,
                                       Message = $"Rescrape job queued. Poll get_scrape_status with jobId='{jobId}' for progress."
                                   };
                json = JsonSerializer.Serialize(response, smJsonOptions);
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

    private const string StatusNoPriorJob = "NoPriorJob";
    private const string StatusRefused = "Refused";
    private const string ReasonUrlSuspect = "URL_SUSPECT";
    private const int DefaultMaxPages = 0;
    private const int DefaultFetchDelayMs = 500;
    private const int RecentJobScanLimit = 100;

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
