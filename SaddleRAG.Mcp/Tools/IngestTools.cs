// IngestTools.cs
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
///     MCP tool exposing the start_ingest state machine — the single front
///     door for ingesting or refreshing a documentation library. Inspects
///     what we already know about (library, version) at a URL and tells the
///     calling LLM what to do next: run reconnaissance, scrape, continue an
///     interrupted scrape, rescrub stale chunks, or query if everything is
///     current.
/// </summary>
[McpServerToolType]
public static class IngestTools
{
    [McpServerTool(Name = "start_ingest")]
    [Description("Single ingestion entrypoint — call this first when you want to ingest or refresh a library. " +
                 "Inspects (library, version) state and returns one of six states: " +
                 "IN_PROGRESS (scrape already running — poll get_scrape_status or call cancel_scrape), " +
                 "URL_SUSPECT (indexed content looks wrong — browse URL and call submit_url_correction), " +
                 "RECON_NEEDED (no profile — call recon_library then submit_library_profile), " +
                 "READY_TO_SCRAPE (profile cached, no chunks — call scrape_docs), " +
                 "STALE (chunks exist but parser-derived metadata is outdated — call reextract_library), " +
                 "READY (fully indexed and current — call search_docs or get_class_reference). " +
                 "Each response includes NextTool and NextToolArgs so you can follow the breadcrumb without remembering the workflow. " +
                 "The auto flag is reserved and ignored today; callers should follow NextTool manually."
                )]
    public static async Task<string> StartIngest(RepositoryFactory repositoryFactory,
                                                 [Description("Root URL of the docs site to ingest")]
                                                 string url,
                                                 [Description("Library identifier (e.g. 'aerotech-aeroscript'). Required for now; URL-based inference is a future enhancement."
                                                             )]
                                                 string library,
                                                 [Description("Library version (e.g. '2025.3'). Required for now.")]
                                                 string version,
                                                 [Description("Reserved for future auto-follow behavior. Ignored today; follow NextTool manually."
                                                             )]
                                                 bool auto = false,
                                                 [Description("If true, return READY_TO_SCRAPE for an otherwise READY library so the caller can intentionally refresh via scrape_docs."
                                                             )]
                                                 bool force = false,
                                                 [Description("Optional database profile name")]
                                                 string? profile = null,
                                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
        var chunkRepo = repositoryFactory.GetChunkRepository(profile);
        var scrapeJobRepo = repositoryFactory.GetScrapeJobRepository(profile);
        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);

        var libraryProfile = await profileRepo.GetAsync(library, version, ct);
        int chunkCount = await chunkRepo.GetChunkCountAsync(library, version, ct);

        var stale = false;
        if (chunkCount > 0)
            stale = await chunkRepo.HasStaleChunksAsync(library, version, ParserVersionInfo.Current, ct);

        var activeJob = await scrapeJobRepo.GetActiveJobAsync(library, version, ct);
        var versionRecord = await libraryRepo.GetVersionAsync(library, version, ct);

        bool isInProgress = activeJob != null;
        bool isSuspect = versionRecord is { Suspect: true };
        string activeJobId = activeJob?.Id ?? string.Empty;
        var suspectReasons = versionRecord?.SuspectReasons ?? [];

        var suspectResponse = isSuspect && !isInProgress
                                  ? await MakeUrlSuspectAsync(library,
                                                              version,
                                                              url,
                                                              suspectReasons,
                                                              chunkRepo,
                                                              ct
                                                             )
                                  : null;

        var response = isInProgress switch
            {
                true => MakeInProgress(library, version, url, activeJobId),
                false => suspectResponse ??
                         ResolveStatus(libraryProfile,
                                       chunkCount,
                                       stale,
                                       library,
                                       version,
                                       url,
                                       force
                                      )
            };

        string json = JsonSerializer.Serialize(response, smJsonOptions);
        return json;
    }

    private static IngestStatusResponse ResolveStatus(LibraryProfile? libraryProfile,
                                                      int chunkCount,
                                                      bool stale,
                                                      string library,
                                                      string version,
                                                      string url,
                                                      bool force)
    {
        bool hasProfile = libraryProfile != null;
        bool hasChunks = chunkCount > 0;
        var excludedPatterns = libraryProfile?.CrawlHints.ExcludedUrlPatterns ?? [];

        var response = (hasProfile, hasChunks, stale, force) switch
            {
                (false, var _, var _, var _) => MakeReconNeeded(library, version, url),
                (true, false, var _, var _) =>
                    MakeReadyToScrape(library, version, url, MessageReadyToScrapeFresh, excludedPatterns),
                (true, true, true, var _) => MakeStale(library, version, url),
                (true, true, false, true) =>
                    MakeReadyToScrape(library, version, url, MessageReadyToScrapeForce, excludedPatterns),
                (true, true, false, false) => MakeReady(library, version, url)
            };

        return response;
    }

    private static IngestStatusResponse MakeInProgress(string library, string version, string url, string jobId) =>
        new IngestStatusResponse
            {
                Status = IngestStatus.InProgress,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "get_scrape_status",
                Message =
                    $"Scrape job {jobId} is already running. Poll get_scrape_status, or call cancel_scrape to abort.",
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["jobId"] = jobId
                                   }
            };

    private static IngestStatusResponse MakeReconNeeded(string library, string version, string url) =>
        new IngestStatusResponse
            {
                Status = IngestStatus.ReconNeeded,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "recon_library",
                Message =
                    "No library profile cached. Call recon_library (args in NextToolArgs) to get the schema and instructions, " +
                    "then browse the docs site and call submit_library_profile with the resulting JSON.",
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["url"] = url,
                                       ["library"] = library,
                                       ["version"] = version
                                   }
            };

    internal static IngestStatusResponse MakeReadyToScrape(string library,
                                                           string version,
                                                           string url,
                                                           string message,
                                                           IReadOnlyList<string> excludedUrlPatterns) =>
        new IngestStatusResponse
            {
                Status = IngestStatus.ReadyToScrape,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "scrape_docs",
                Message = message,
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["url"] = url,
                                       ["libraryId"] = library,
                                       ["version"] = version
                                   },
                RecommendedExcludedUrlPatterns = excludedUrlPatterns
            };

    private static IngestStatusResponse MakeStale(string library, string version, string url) =>
        new IngestStatusResponse
            {
                Status = IngestStatus.Stale,
                LibraryId = library,
                Version = version,
                Url = url,
                NextTool = "reextract_library",
                Message = "Chunks exist, but their parser-derived metadata is stale. " +
                          "Call reextract_library to re-extract metadata from the stored chunks and rebuild indexes " +
                          "without re-crawling, re-chunking, or re-embedding.",
                NextToolArgs = new Dictionary<string, string>
                                   {
                                       ["library"] = library,
                                       ["version"] = version
                                   }
            };

    private static IngestStatusResponse MakeReady(string library, string version, string url) =>
        new IngestStatusResponse
            {
                Status = IngestStatus.Ready,
                LibraryId = library,
                Version = version,
                Url = url,
                Message =
                    "Profile cached, index built, current parser version. Ready to query — call search_docs for natural-language search, get_class_reference for a specific type, or get_library_overview for an introduction."
            };

    private static async Task<IngestStatusResponse> MakeUrlSuspectAsync(string library,
                                                                        string version,
                                                                        string url,
                                                                        IReadOnlyList<string> suspectReasons,
                                                                        IChunkRepository chunkRepo,
                                                                        CancellationToken ct)
    {
        var sampleTitles = await chunkRepo.GetSampleTitlesAsync(library, version, UrlSuspectSampleTitleLimit, ct);
        var hostnameDist = await chunkRepo.GetHostnameDistributionAsync(library, version, ct);

        string sampleTitlesJoined = string.Join(SemicolonSeparator, sampleTitles.Take(UrlSuspectSampleTitlesShown));
        string hostnamesJoined = string.Join(CommaSeparator, hostnameDist.Keys.Take(UrlSuspectHostnamesShown));
        string reasonsJoined = string.Join(CommaSeparator, suspectReasons);

        var result = new IngestStatusResponse
                         {
                             Status = IngestStatus.UrlSuspect,
                             LibraryId = library,
                             Version = version,
                             Url = url,
                             NextTool = "submit_url_correction",
                             Message = $"Indexed content looks wrong: {reasonsJoined}. " +
                                       $"Sample titles: {sampleTitlesJoined}. " +
                                       $"Hostnames: {hostnamesJoined}. " +
                                       "Browse the URL and call submit_url_correction with a better one if needed. " +
                                       "The library and version are pre-filled in NextToolArgs; you must supply newUrl yourself after browsing.",
                             NextToolArgs = new Dictionary<string, string>
                                                {
                                                    ["library"] = library,
                                                    ["version"] = version
                                                }
                         };
        return result;
    }

    private const string MessageExcludedPatternHint =
        "If RecommendedExcludedUrlPatterns is non-empty, pass it as scrape_docs.excludedUrlPatterns.";

    private const string MessageReadyToScrapeFresh =
        "Profile cached, no chunks indexed. Call scrape_docs (args in NextToolArgs) to begin ingestion. " +
        MessageExcludedPatternHint;

    private const string MessageReadyToScrapeForce =
        "force=true: index exists but caller requested re-ingest. Call scrape_docs (args in NextToolArgs) to refresh. " +
        MessageExcludedPatternHint;

    private const int UrlSuspectSampleTitleLimit = 5;
    private const int UrlSuspectSampleTitlesShown = 3;
    private const int UrlSuspectHostnamesShown = 5;
    private const string SemicolonSeparator = "; ";
    private const string CommaSeparator = ", ";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
