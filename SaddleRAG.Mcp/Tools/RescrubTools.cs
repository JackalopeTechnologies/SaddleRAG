// RescrubTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tool exposing reextract_library — the lightweight metadata-refresh path
///     over chunks already stored in MongoDB.
/// </summary>
[McpServerToolType]
public static class RescrubTools
{
    [McpServerTool(Name = "reextract_library")]
    [Description("Queue a background job that re-extracts parser-derived metadata from chunks already stored for " +
                 "(library, version). Does NOT re-crawl, re-chunk, or re-embed. Updates Symbols[], QualifiedName, " +
                 "ParserVersion, optional Category reclassification, and the derived library_indexes " +
                 "(CodeFenceSymbols + Manifest). Use this when parser, extractor, classifier, or index-building logic " +
                 "changed but the stored chunk text is still valid. Returns a JobId immediately — poll " +
                 "get_reextract_status(jobId) for completion. When done the result includes counts, a sample of per-chunk " +
                 "diffs, and a BoundaryHint field: null (healthy), 'rechunk_library may help' (5%–10% boundary issues), " +
                 "or 'rechunk_library recommended' (≥10%). Act on the hint before calling search_docs. Idempotent and " +
                 "resumable. If no LibraryProfile exists yet, the completed result will contain RECON_NEEDED — call " +
                 "recon_library and submit_library_profile first."
                )]
    public static async Task<string> RescrubLibrary(RescrubJobRunner runner,
                                                    [Description("Library identifier (e.g. 'aerotech-aeroscript')")]
                                                    string library,
                                                    [Description("Library version (e.g. '2025.3')")]
                                                    string version,
                                                    [Description("If true, reports what would change without writing to MongoDB."
                                                                )]
                                                    bool dryRun = false,
                                                    [Description("Force reclassification even when auto-detect would skip it. Omit to auto-decide from manifest history."
                                                                )]
                                                    bool? reclassify = null,
                                                    [Description("Skip the pre-flight chunk-boundary audit (typically only used for tests)."
                                                                )]
                                                    bool skipBoundaryAudit = false,
                                                    [Description("If true (default), rebuild CodeFenceSymbols and Manifest."
                                                                )]
                                                    bool rebuildIndexes = true,
                                                    [Description("Optional cap for spot-checking large libraries.")]
                                                    int? maxChunks = null,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var options = new RescrubOptions
                          {
                              DryRun = dryRun,
                              ReClassify = reclassify,
                              BoundaryAudit = !skipBoundaryAudit,
                              RebuildIndexes = rebuildIndexes,
                              MaxChunks = maxChunks
                          };

        var jobId = await runner.QueueAsync(library, version, options, profile, ct);

        var response = new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
