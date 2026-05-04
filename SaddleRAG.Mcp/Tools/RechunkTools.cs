// RechunkTools.cs
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
using SaddleRAG.Ingestion.Recon;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool exposing <c>rechunk_library</c> — refresh an already-ingested
///     library against the current chunker code, without re-crawling. Use this
///     after a chunker change ships and existing libraries should benefit
///     without paying the cost of a full re-ingest.
/// </summary>
[McpServerToolType]
public static class RechunkTools
{
    [McpServerTool(Name = "rechunk_library")]
    [Description("Re-run the chunker over pages already stored for (library, version). " +
                 "Replaces all chunks and re-embeds, then requires rescrub_library as a mandatory follow-up " +
                 "to populate corpus-aware Symbols[] and rebuild library_indexes — do not skip this. " +
                 "NO re-crawl, NO re-classify. Use after a chunker code change when existing chunks should " +
                 "be re-cut without re-fetching the docs site. Returns { JobId, Status: 'Queued' } immediately; " +
                 "poll get_job_status for progress and results including BoundaryHint."
                )]
    public static async Task<string> RechunkLibrary(RepositoryFactory repositoryFactory,
                                                    RechunkService rechunkService,
                                                    [FromKeyedServices(nameof(IBackgroundJobRunner))]
                                                    IBackgroundJobRunner runner,
                                                    [Description("Library identifier (e.g. 'aerotech-aeroscript')")]
                                                    string library,
                                                    [Description("Library version (e.g. '1.0')")]
                                                    string version,
                                                    [Description("If true, reports what would change without writing to MongoDB or touching the vector index."
                                                                )]
                                                    bool dryRun = false,
                                                    [Description("Skip the chunk-boundary audit. Default false; the audit is the primary signal that the new chunker code did its job."
                                                                )]
                                                    bool skipBoundaryAudit = false,
                                                    [Description("Optional cap for spot-checking large libraries.")]
                                                    int? maxPages = null,
                                                    [Description("Optional database profile name (use list_profiles to discover)"
                                                                )]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(rechunkService);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var options = new RechunkOptions
                          {
                              DryRun = dryRun,
                              BoundaryAudit = !skipBoundaryAudit,
                              MaxPages = maxPages
                          };

        var inputJson =
            JsonSerializer.Serialize(new { library, version, dryRun, skipBoundaryAudit, maxPages, profile });
        var jobRecord = new BackgroundJobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = BackgroundJobTypes.Rechunk,
                                Profile = profile,
                                LibraryId = library,
                                Version = version,
                                InputJson = inputJson,
                                ItemsLabel = ItemsLabelChunks
                            };

        var jobId = await runner.QueueAsync(jobRecord,
                                            async (record, onProgress, jobCt) =>
                                            {
                                                var pageRepo = repositoryFactory.GetPageRepository(profile);
                                                var chunkRepo = repositoryFactory.GetChunkRepository(profile);
                                                var profileRepo =
                                                    repositoryFactory.GetLibraryProfileRepository(profile);
                                                var result = await rechunkService.RechunkAsync(profile,
                                                                      pageRepo,
                                                                      chunkRepo,
                                                                      profileRepo,
                                                                      library,
                                                                      version,
                                                                      options,
                                                                      onProgress,
                                                                      jobCt
                                                                 );
                                                record.ResultJson = JsonSerializer.Serialize(result, smJsonOptions);
                                            },
                                            ct
                                           );

        var response = new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    private const string ItemsLabelChunks = "chunks";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
