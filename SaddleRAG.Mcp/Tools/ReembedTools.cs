// ReembedTools.cs
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
///     MCP tool exposing reembed_library — the path for swapping embedding
///     vectors on already-stored chunks without re-crawling or re-chunking.
/// </summary>
[McpServerToolType]
public static class ReembedTools
{
    [McpServerTool(Name = "reembed_library")]
    [Description("Queue a background job that re-embeds every stored chunk for (library, version) using the " +
                 "currently configured embedding provider and model. Does NOT re-crawl, re-chunk, or refresh " +
                 "parser-derived metadata. Use this after switching the embedding provider (e.g. swapping " +
                 "Ollama models, or moving to a different provider) so existing libraries get vectors that are " +
                 "compatible with live query embeddings. Updates LibraryVersionRecord.EmbeddingProviderId / " +
                 "EmbeddingModelName / EmbeddingDimensions on success. Returns a JobId immediately — poll " +
                 "get_reembed_status(jobId) for completion. Idempotent: safe to run repeatedly."
                )]
    public static async Task<string> ReembedLibrary(ReembedJobRunner runner,
                                                    [Description("Library identifier (e.g. 'aerotech-aeroscript')")]
                                                    string library,
                                                    [Description("Library version (e.g. '2025.3')")]
                                                    string version,
                                                    [Description("If true, reports what would change without writing to MongoDB or reloading the vector index."
                                                                )]
                                                    bool dryRun = false,
                                                    [Description("Optional cap for spot-checking large libraries.")]
                                                    int? maxChunks = null,
                                                    [Description("Optional database profile name")]
                                                    string? profile = null,
                                                    CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var options = new ReembedOptions
                          {
                              DryRun = dryRun,
                              MaxChunks = maxChunks
                          };

        var jobId = await runner.QueueAsync(library, version, options, profile, ct);

        var response = new { JobId = jobId, Status = nameof(ScrapeJobStatus.Queued) };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
