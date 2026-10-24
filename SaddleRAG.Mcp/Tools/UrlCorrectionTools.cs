// UrlCorrectionTools.cs
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
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Scanning;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool for re-rooting an unpublished suspect scrape at a corrected URL.
///     Recon-style callback after start_ingest reports URL_SUSPECT:
///     drops the existing chunks/pages/profile/indexes/shards, clears
///     LibraryVersion.Suspect, then queues a fresh scrape_docs at the
///     corrected URL. The apply path (dryRun=false) runs in a background
///     job so the MCP transport is not blocked.
/// </summary>
[McpServerToolType]
public static class UrlCorrectionTools
{
    [McpServerTool(Name = "submit_url_correction")]
    [Description("Re-root an unpublished partial scrape at a corrected URL. Published targets return " +
                 "Refused/PUBLISHED_VERSION without deleting or queueing; use a new version identifier for a full scrape instead. Drops the existing chunks, " +
                 "pages, profile, indexes, and bm25 shards for (library, version), " +
                 "clears the Suspect flag, then queues a fresh scrape_docs at newUrl. " +
                 "Use when start_ingest returned URL_SUSPECT or when scrape_docs(resume=true) " +
                 "returned Status=Refused with Reason=URL_SUSPECT — both indicate the indexed " +
                 "content is probably wrong. Browse the URL yourself first to confirm a better one. " +
                 "dryRun=false (default) queues a background job and returns { JobId, Status: 'Queued' } " +
                 "immediately; poll get_job_status for the scrape JobId to chain to get_scrape_status. " +
                 "Pass dryRun=true to preview."
                )]
    public static async Task<string> SubmitUrlCorrection(RepositoryFactory repositoryFactory,
                                                         ScrapeJobRunner scrapeRunner,
                                                         IBackgroundJobRunner backgroundRunner,
                                                         JobCancellationService cancellation,
                                                         ILibraryIngestionModeLeaseManager modeLeaseManager,
                                                         [Description("Library identifier")] string library,
                                                         [Description("Version")] string version,
                                                         [Description("Corrected docs root URL")]
                                                         string newUrl,
                                                         [Description("If true, preview without writing or queueing.")]
                                                         bool dryRun = false,
                                                         [Description("Optional database profile name")]
                                                         string? profile = null,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(scrapeRunner);
        ArgumentNullException.ThrowIfNull(backgroundRunner);
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(modeLeaseManager);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(newUrl);

        var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
        var versionRecord = await libraryRepo.GetVersionAsync(library, version, ct);
        string result;
        if (versionRecord?.PublicationState == VersionPublicationState.Published)
        {
            result = PublishedVersionRefusal.Create(library, version);
        }
        else
        {
            if (dryRun)
            {
                var chunkRepo = repositoryFactory.GetChunkRepository(profile);
                var pageRepo = repositoryFactory.GetPageRepository(profile);
                var jobRepo = repositoryFactory.GetJobRepository(profile);

                var chunks = await chunkRepo.GetChunkCountAsync(library, version, ct);
                var pages = await pageRepo.GetPageCountAsync(library, version, ct);
                var activeJobs = await jobRepo.ListActiveAsync(library, version, JobType.Scrape, ct);
                var preview = new
                                  {
                                      DryRun = true,
                                      WouldDelete = new
                                                        {
                                                            Chunks = chunks, Pages = pages, Profiles = 1, Indexes = 1,
                                                            Bm25Shards = 1
                                                        },
                                      WouldCancel = activeJobs
                                                    .Select(j => new { j.Id, j.Status, PipelineState = j.Status.ToString() }
                                                           )
                                                    .ToList(),
                                      WouldQueue = new { RootUrl = newUrl, Library = library, Version = version }
                                  };
                result = JsonSerializer.Serialize(preview, smJsonOptions);
            }
            else
            {
                var inputJson = JsonSerializer.Serialize(new { library, version, newUrl, profile });
                var jobRecord = new BackgroundJobRecord
                                    {
                                        Id = Guid.NewGuid().ToString(),
                                        JobType = BackgroundJobTypes.SubmitUrlCorrection,
                                        Profile = profile,
                                        LibraryId = library,
                                        Version = version,
                                        InputJson = inputJson
                                    };

                var jobId = await backgroundRunner.QueueAsync(jobRecord,
                                                              async (record, _, jobCt) =>
                                                              {
                                                                  await PublishedVersionRefusal.EnsureUnpublishedAsync(libraryRepo, library, version, jobCt);
                                                                  var jobRepo =
                                                                      repositoryFactory.GetJobRepository(profile);
                                                                  var activeJobs =
                                                                      await jobRepo.ListActiveAsync(library,
                                                                               version,
                                                                               JobType.Scrape,
                                                                               jobCt
                                                                          );
                                                                  var cancelledIds = new List<string>();
                                                                  foreach(var existing in activeJobs)
                                                                  {
                                                                      await cancellation.CancelAsync(existing.Id,
                                                                                                     profile,
                                                                                                     jobCt
                                                                                                    );
                                                                      cancelledIds.Add(existing.Id);
                                                                  }

                                                                  // Release the cleanup lease before the queued scrape acquires its own.
                                                                  (long chunks, long pages) = await ClearUnpublishedAsync(repositoryFactory,
                                                                      modeLeaseManager, library, version, profile, jobCt);

                                                                  var scrapeJob = ScrapeJobFactory.CreateFromUrl(newUrl,
                                                                           library,
                                                                           version,
                                                                           CorrectedHint,
                                                                           forceClean: true
                                                                      );
                                                                  var scrapeJobId =
                                                                      await scrapeRunner.QueueAsync(scrapeJob,
                                                                               profile,
                                                                               jobCt
                                                                          );

                                                                  record.ResultJson = JsonSerializer.Serialize(new
                                                                               {
                                                                                   Cleared = new
                                                                                       {
                                                                                           Chunks = chunks,
                                                                                           Pages = pages
                                                                                       },
                                                                                   CancelledJobs = cancelledIds,
                                                                                   ScrapeJobId = scrapeJobId,
                                                                                   Message =
                                                                                       $"Suspect chunks dropped, scrape re-queued at {newUrl}. Poll get_scrape_status with jobId='{scrapeJobId}'."
                                                                               },
                                                                           smJsonOptions
                                                                      );
                                                              },
                                                              ct
                                                             );

                result = JsonSerializer.Serialize(new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) },
                                                  smJsonOptions
                                                 );
            }
        }

        return result;
    }

    private static async Task<(long Chunks, long Pages)> ClearUnpublishedAsync(
        RepositoryFactory repositoryFactory,
        ILibraryIngestionModeLeaseManager modeLeaseManager,
        string library,
        string version,
        string? profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(modeLeaseManager);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ILibraryIngestionModeLease? acquired = await modeLeaseManager.TryAcquireAsync(profile,
            library, LibraryIngestionMode.Web, ct);
        if (acquired is null)
            throw new InvalidOperationException("URL correction did not change data: the library is busy or owned by directory ingestion. Retry after active work finishes.");
        await using ILibraryIngestionModeLease lease = acquired;
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.OwnershipLostToken);
        ILibraryRepository libraries = repositoryFactory.GetLibraryRepository(profile);
        await PublishedVersionRefusal.EnsureUnpublishedAsync(libraries, library, version, operation.Token);
        if (!await lease.TryRenewAsync(operation.Token))
            throw new InvalidOperationException("URL correction did not change data: its library ownership lease was lost.");
        operation.Token.ThrowIfCancellationRequested();

        long chunks = await repositoryFactory.GetChunkRepository(profile).DeleteChunksAsync(library, version, operation.Token);
        long pages = await repositoryFactory.GetPageRepository(profile).DeleteAsync(library, version, operation.Token);
        await repositoryFactory.GetLibraryProfileRepository(profile).DeleteAsync(library, version, operation.Token);
        await repositoryFactory.GetLibraryIndexRepository(profile).DeleteAsync(library, version, operation.Token);
        await repositoryFactory.GetBm25ShardRepository(profile).DeleteAsync(library, version, operation.Token);
        await repositoryFactory.GetScrapeAuditRepository(profile).DeleteByLibraryVersionAsync(library, version, operation.Token);
        await libraries.ClearSuspectAsync(library, version, operation.Token);
        return (chunks, pages);
    }

    private const string CorrectedHint = "(corrected URL)";
    private const int DefaultMaxPages = 0;

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
