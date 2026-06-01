// RechunkTools.cs
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
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Recon;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool exposing <c>rechunk_library</c> — rebuild chunks from stored pages
///     without fetching the source site again.
/// </summary>
[McpServerToolType]
public static class RechunkTools
{
    [McpServerTool(Name = "rechunk_library")]
    [Description("Reuse pages already stored for (library, version), run the current chunker over them again, replace all " +
                 "chunks, and re-embed those new chunks. Does NOT fetch the source site again. Use this when chunk boundaries " +
                 "or the chunk-to-embedding input changed and you want a local rebuild from stored pages instead of a full " +
                 "re-scrape. This currently requires a reextract_library follow-up to refresh parser-derived metadata and rebuild " +
                 "library_indexes. Returns { JobId, Status: 'Queued' } immediately; poll get_job_status for progress and " +
                 "results including BoundaryHint."
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
