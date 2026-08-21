// HealthTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for diagnostic visibility into a library's index state.
///     get_library_health surfaces chunk count, language mix, hostnames,
///     boundary-issue rate, suspect markers — distinct from
///     get_library_overview, which returns the actual library content.
/// </summary>
[McpServerToolType]
public static class HealthTools
{
    [McpServerTool(Name = "get_library_health")]
    [Description("Per-version diagnostic snapshot. Returns chunk count, hostname " +
                 "distribution, language mix, boundary-issue rate, suspect markers, and " +
                 "duplicateContentPct with a contentSuspect flag (near-identical content across " +
                 "pages - the extractor likely captured a boilerplate widget). Also returns a " +
                 "suggestedNextAction field (rescrape_library if contentSuspect, " +
                 "submit_url_correction if suspect, rechunk_library if boundaryIssuePct >= 10%, " +
                 "null if healthy). For the actual library content, use get_library_overview instead."
                )]
    public static async Task<string> GetLibraryHealth(RepositoryFactory repositoryFactory,
                                                      [Description("Library identifier")] string library,
                                                      [Description("Specific version — defaults to current")]
                                                      string? version = null,
                                                      [Description("Optional database profile name")]
                                                      string? profile = null,
                                                      CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);

        var lib = await libraryRepo.GetLibraryAsync(library, ct);
        string result;
        if (lib == null)
            result = JsonSerializer.Serialize(new { Error = $"Library '{library}' not found." }, smJsonOptions);
        else
        {
            result = await BuildHealthResponseAsync(library,
                                                    lib,
                                                    version,
                                                    chunkRepo,
                                                    libraryRepo,
                                                    ct
                                                   );
        }

        return result;
    }

    private static async Task<string> BuildHealthResponseAsync(string library,
                                                               LibraryRecord lib,
                                                               string? version,
                                                               IChunkRepository chunkRepo,
                                                               ILibraryRepository libraryRepo,
                                                               CancellationToken ct)
    {
        string resolvedVersion = version ?? lib.CurrentVersion;
        var versionRecord = await libraryRepo.GetVersionAsync(library, resolvedVersion, ct);

        string result;
        if (versionRecord?.PublicationState != VersionPublicationState.Published)
            result = JsonSerializer.Serialize(new
                                                   {
                                                       Error =
                                                           $"Version '{resolvedVersion}' not found or not published."
                                                   },
                                               smJsonOptions
                                              );
        else
        {
            result = await BuildVersionSnapshotAsync(library,
                                                     lib,
                                                     resolvedVersion,
                                                     versionRecord,
                                                     chunkRepo,
                                                     ct
                                                    );
        }

        return result;
    }

    private static async Task<string> BuildVersionSnapshotAsync(string library,
                                                                LibraryRecord lib,
                                                                string resolvedVersion,
                                                                LibraryVersionRecord versionRecord,
                                                                IChunkRepository chunkRepo,
                                                                CancellationToken ct)
    {
        var languageMix = await chunkRepo.GetLanguageMixAsync(library, resolvedVersion, ct);
        var hostnames = await chunkRepo.GetHostnameDistributionAsync(library, resolvedVersion, ct);
        var contentSample = await chunkRepo.GetContentSampleAsync(library,
                                                                  resolvedVersion,
                                                                  DuplicateContentSampleCap,
                                                                  ct);
        (string? boundaryHint, string? boundaryHintMessage) = ResolveBoundaryHint(versionRecord.BoundaryIssuePct);
        (double duplicateContentPct, int contentSampleSize, bool contentSuspect) =
            EvaluateContentDuplication(contentSample);
        object suggestedNextAction = ResolveSuggestedNextAction(contentSuspect, versionRecord);

        var hostnamesProjection = hostnames.OrderByDescending(kv => kv.Value)
                                           .Take(MaxHostnamesReturned)
                                           .Select(kv => new { host = kv.Key, count = kv.Value })
                                           .ToList();

        var response = new
                           {
                               library,
                               version = resolvedVersion,
                               currentVersion = lib.CurrentVersion,
                               lastScrapedAt = versionRecord.ScrapedAt,
                               chunkCount = versionRecord.ChunkCount,
                               pageCount = versionRecord.PageCount,
                               distinctHostCount = hostnames.Count,
                               hostnames = hostnamesProjection,
                               languageMix,
                               boundaryIssuePct = versionRecord.BoundaryIssuePct,
                               suspect = versionRecord.Suspect,
                               suspectReasons = versionRecord.SuspectReasons,
                               boundaryHint = new { hint = boundaryHint, message = boundaryHintMessage },
                               duplicateContentPct = Math.Round(duplicateContentPct, 1),
                               contentSampleSize,
                               contentSuspect,
                               suggestedNextAction
                           };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    private static (string? hint, string? message) ResolveBoundaryHint(double pct) => pct switch
        {
            >= BoundaryHintRecommendThreshold => (BoundaryHintRecommendedKey, BoundaryHintRecommendedMessage),
            >= BoundaryHintMayHelpThreshold => (BoundaryHintMayHelpKey, BoundaryHintMayHelpMessage),
            var _ => (null, null)
        };

    /// <summary>
    ///     Fraction of the sampled chunks whose whitespace-normalized text is a duplicate of
    ///     another sampled chunk, plus a contentSuspect verdict. A library whose every page was
    ///     reduced to the same boilerplate widget clusters to one distinct text, so the fraction
    ///     approaches 100%. Computed from a bounded sample, so it catches libraries mis-ingested
    ///     under an older extractor without re-scraping them.
    /// </summary>
    private static (double duplicatePct, int sampleSize, bool contentSuspect) EvaluateContentDuplication(
        IReadOnlyList<string>? sample)
    {
        double duplicatePct = 0;
        int sampleSize = sample?.Count ?? 0;
        bool contentSuspect = false;
        if (sample is { Count: > 0 })
        {
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach(string content in sample)
                distinct.Add(NormalizeWhitespace(content));
            duplicatePct = 100.0 * (sampleSize - distinct.Count) / sampleSize;
            contentSuspect = sampleSize >= MinContentSampleForSuspect &&
                             duplicatePct >= DuplicateContentSuspectThreshold;
        }

        return (duplicatePct, sampleSize, contentSuspect);
    }

    private static string NormalizeWhitespace(string content)
    {
        string[] parts = content.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries);
        string result = string.Join(' ', parts);
        return result;
    }

    private static object ResolveSuggestedNextAction(bool contentSuspect, LibraryVersionRecord versionRecord) =>
        (contentSuspect,
         versionRecord.Suspect,
         versionRecord.BoundaryIssuePct >= BoundaryHintRecommendThreshold) switch
            {
                (true, var _, var _) => new { tool = (string?) SuggestToolRescrape, message = ContentSuspectSuggestion },
                (var _, true, var _) => new { tool = (string?) SuggestToolCorrectUrl, message = SuspectSuggestion },
                (var _, var _, true) => new { tool = (string?) SuggestToolRechunk, message = BoundaryRechunkSuggestion },
                var _ => new { tool = (string?) null, message = HealthySuggestion }
            };

    [McpServerTool(Name = "get_dashboard_index")]
    [McpMeta("anthropic/alwaysLoad", value: true)]
    [Description("Start here in any fresh or disoriented session. Returns a single-call " +
                 "SaddleRAG status overview: the running serverVersion (report it verbatim when " +
                 "asked which SaddleRAG is running), library/version counts, recent scrape jobs (with " +
                 "recentJobs[].Stale=true for Running jobs that haven't progressed in 4+ hours), and up to " +
                 "20 suspect libraries. The SuggestedNextAction field always contains the highest-priority " +
                 "tool to call next (scrape_docs for empty DB, submit_url_correction for suspect libraries, " +
                 "cancel_job for stale-running jobs, null when healthy). If a recent job is marked Stale, call cancel_job with that job id. Act on SuggestedNextAction " +
                 "before doing anything else."
                )]
    public static async Task<string> GetDashboardIndex(RepositoryFactory repositoryFactory,
                                                       McpWarmupState warmupState,
                                                       [Description("Optional database profile name")]
                                                       string? profile = null,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(warmupState);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var jobRepo = repositoryFactory.GetJobRepository(profile);

        var libraries = await libraryRepo.GetAllLibrariesAsync(ct);
        var recentJobs = await jobRepo.ListRecentAsync(JobType.Scrape, RecentJobsLimit, ct);
        // Stale-running detection has to cover every long-running job type,
        // not just Scrape — a Reembed or Rescrub that died with the process
        // (e.g. the DirectML TDR crashes from May 14, 2026) leaves a Running
        // row that the runner's catch block never got to mark Failed. Without
        // this widening the dashboard wouldn't surface the orphan and
        // start_ingest's active-job check would still see it as live.
        var runningJobs = await jobRepo.ListRunningAsync(jobType: null, ct);
        var mergedJobs = MergeRecentAndRunning(recentJobs, runningJobs);

        var suspectList = new List<object>();
        var versionCount = 0;
        foreach(var lib in libraries)
        {
            foreach(string v in lib.AllVersions)
            {
                versionCount++;
                var versionRecord = await libraryRepo.GetVersionAsync(lib.Id, v, ct);
                if (versionRecord is { Suspect: true } && suspectList.Count < SuspectListCap)
                    suspectList.Add(new { library = lib.Id, version = v, reasons = versionRecord.SuspectReasons });
            }
        }

        var staleCutoff = DateTime.UtcNow - ScrapeJobThresholds.StaleRunning;
        var recentJobsProjection = mergedJobs.Select(j => new
                                                              {
                                                                  j.Id,
                                                                  JobType = j.JobType.ToString(),
                                                                  Status = j.Status.ToString(),
                                                                  PipelineState = j.Status.ToString(),
                                                                  Library = j.LibraryId,
                                                                  j.Version,
                                                                  Stale =
                                                                      ScrapeJobThresholds
                                                                          .IsStaleRunning(j, staleCutoff),
                                                                  j.LastProgressAt
                                                              }
                                                    )
                                             .ToList();

        int staleRunning = mergedJobs.Count(j => ScrapeJobThresholds.IsStaleRunning(j, staleCutoff));

        bool warmupFailed = string.Equals(warmupState.Status, WarmupStatusFailed, StringComparison.OrdinalIgnoreCase);

        object suggested = (warmupFailed, libraries.Count == 0, suspectList.Count > 0, staleRunning > 0) switch
            {
                (true, var _, var _, var _) => new
                                                   {
                                                       tool = (string?) null,
                                                       message =
                                                           $"Warmup did not complete (phase: {warmupState.CurrentPhase}). Restart Ollama (kill duplicate ollama processes), then restart the MCP server. LastError: {warmupState.LastError ?? "(none)"}"
                                                   },
                (var _, true, var _, var _) => new { tool = (string?) SuggestToolScrape, message = EmptyDbSuggestion },
                (var _, var _, true, var _) => new
                                                   {
                                                       tool = (string?) SuggestToolCorrectUrl,
                                                       message =
                                                           $"{suspectList.Count} suspect libraries — review and correct URLs."
                                                   },
                (var _, var _, var _, true) => new
                                                   {
                                                       tool = (string?) SuggestToolCancelScrape,
                                                       message =
                                                           $"{staleRunning} jobs have not progressed in over {ScrapeJobThresholds.StaleRunning.TotalHours}h."
                                                   },
                var _ => new { tool = (string?) null, message = SuggestMessageHealthy }
            };

        var warmup = new
                         {
                             status = warmupState.Status,
                             currentPhase = warmupState.CurrentPhase,
                             lastError = warmupState.LastError
                         };

        var response = new
                           {
                               serverVersion = SaddleRagVersion.Informational,
                               libraryCount = libraries.Count,
                               versionCount,
                               recentJobs = recentJobsProjection,
                               suspectCount = suspectList.Count,
                               suspectLibraries = suspectList,
                               warmup,
                               suggestedNextAction = suggested
                           };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    private static IReadOnlyList<JobRecord> MergeRecentAndRunning(IReadOnlyList<JobRecord> recent,
                                                                   IReadOnlyList<JobRecord> running)
    {
        var byId = new Dictionary<string, JobRecord>(StringComparer.Ordinal);
        foreach(var job in recent)
            byId[job.Id] = job;
        foreach(var job in running)
            byId.TryAdd(job.Id, job);
        var result = byId.Values
                         .OrderByDescending(j => j.CreatedAt)
                         .ToList();
        return result;
    }

    private const int MaxHostnamesReturned = 20;
    private const double BoundaryHintMayHelpThreshold = 5.0;
    private const double BoundaryHintRecommendThreshold = 10.0;
    private const string BoundaryHintRecommendedKey = "rechunk_recommended";
    private const string BoundaryHintRecommendedMessage = "rechunk_library recommended";
    private const string BoundaryHintMayHelpKey = "rechunk_may_help";
    private const string BoundaryHintMayHelpMessage = "rechunk_library may help";
    private const int RecentJobsLimit = 5;
    private const int SuspectListCap = 20;
    private const string EmptyDbSuggestion = "Database is empty. Ingest a library to begin.";
    private const string SuggestToolScrape = "scrape_docs";
    private const string SuggestToolCorrectUrl = "submit_url_correction";
    private const string SuggestToolCancelScrape = "cancel_job";
    private const string SuggestMessageHealthy = "All libraries look healthy.";
    private const string WarmupStatusFailed = "Failed";
    private const int DuplicateContentSampleCap = 200;
    private const int MinContentSampleForSuspect = 5;
    private const double DuplicateContentSuspectThreshold = 60.0;
    private const string SuggestToolRescrape = "rescrape_library";
    private const string SuggestToolRechunk = "rechunk_library";
    private const string ContentSuspectSuggestion = "Most pages hold near-identical content - extraction likely captured a boilerplate widget instead of the article. Raw HTML is not retained, so reextract/rechunk cannot recover it; rescrape (scrape_docs force=true, optionally with contentSelector) to re-run extraction.";
    private const string SuspectSuggestion = "Library is marked suspect - review the source and call submit_url_correction with a corrected URL.";
    private const string BoundaryRechunkSuggestion = "Chunk boundary issues are high - rechunk_library is recommended.";
    private const string HealthySuggestion = "Library looks healthy.";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
