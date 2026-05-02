// UrlCorrectionTools.cs
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
using SaddleRAG.Ingestion.Scanning;
using ModelContextProtocol.Server;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool for re-rooting a suspect scrape at a corrected URL.
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
    [Description("Re-root a scrape at a corrected URL. Drops the existing chunks, " +
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
                                                         [Description("Library identifier")]
                                                         string library,
                                                         [Description("Version")]
                                                         string version,
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
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(newUrl);

        string result;
        if (dryRun)
        {
            var chunkRepo = repositoryFactory.GetChunkRepository(profile);
            var pageRepo = repositoryFactory.GetPageRepository(profile);
            var scrapeJobRepo = repositoryFactory.GetScrapeJobRepository(profile);

            var chunks = await chunkRepo.GetChunkCountAsync(library, version, ct);
            var pages = await pageRepo.GetPageCountAsync(library, version, ct);
            var activeJobs = await scrapeJobRepo.ListActiveJobsAsync(library, version, ct);
            var preview = new
                              {
                                  DryRun = true,
                                  WouldDelete = new { Chunks = chunks, Pages = pages, Profiles = 1, Indexes = 1, Bm25Shards = 1 },
                                  WouldCancel = activeJobs.Select(j => new { j.Id, j.Status, PipelineState = j.Status.ToString() }).ToList(),
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
                                                              var scrapeJobRepo = repositoryFactory.GetScrapeJobRepository(profile);
                                                              var activeJobs = await scrapeJobRepo.ListActiveJobsAsync(library, version, jobCt);
                                                              var cancelledIds = new List<string>();
                                                              foreach (var existing in activeJobs)
                                                              {
                                                                  await scrapeRunner.CancelAsync(existing.Id, jobCt);
                                                                  cancelledIds.Add(existing.Id);
                                                              }

                                                              var chunkRepo = repositoryFactory.GetChunkRepository(profile);
                                                              var pageRepo = repositoryFactory.GetPageRepository(profile);
                                                              var profileRepo = repositoryFactory.GetLibraryProfileRepository(profile);
                                                              var indexRepo = repositoryFactory.GetLibraryIndexRepository(profile);
                                                              var bm25Repo = repositoryFactory.GetBm25ShardRepository(profile);
                                                              var libraryRepo = repositoryFactory.GetLibraryRepository(profile);
                                                              var scrapeAuditRepo = repositoryFactory.GetScrapeAuditRepository(profile);

                                                              var chunks = await chunkRepo.DeleteChunksAsync(library, version, jobCt);
                                                              var pages = await pageRepo.DeleteAsync(library, version, jobCt);
                                                              await profileRepo.DeleteAsync(library, version, jobCt);
                                                              await indexRepo.DeleteAsync(library, version, jobCt);
                                                              await bm25Repo.DeleteAsync(library, version, jobCt);
                                                              await scrapeAuditRepo.DeleteByLibraryVersionAsync(library, version, jobCt);
                                                              await libraryRepo.ClearSuspectAsync(library, version, jobCt);

                                                              var scrapeJob = ScrapeJobFactory.CreateFromUrl(newUrl,
                                                                                                             library,
                                                                                                             version,
                                                                                                             hint: CorrectedHint,
                                                                                                             maxPages: DefaultMaxPages,
                                                                                                             fetchDelayMs: ScrapeJob.DefaultFetchDelayMs,
                                                                                                             forceClean: true);
                                                              var scrapeJobId = await scrapeRunner.QueueAsync(scrapeJob, profile, jobCt);

                                                              record.ResultJson = JsonSerializer.Serialize(
                                                                  new
                                                                  {
                                                                      Cleared = new { Chunks = chunks, Pages = pages },
                                                                      CancelledJobs = cancelledIds,
                                                                      ScrapeJobId = scrapeJobId,
                                                                      Message = $"Suspect chunks dropped, scrape re-queued at {newUrl}. Poll get_scrape_status with jobId='{scrapeJobId}'."
                                                                  },
                                                                  smJsonOptions);
                                                          },
                                                          ct);

            result = JsonSerializer.Serialize(new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) }, smJsonOptions);
        }

        return result;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const string CorrectedHint = "(corrected URL)";
    private const int DefaultMaxPages = 0;
}
