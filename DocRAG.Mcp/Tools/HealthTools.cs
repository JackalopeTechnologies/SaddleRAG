// HealthTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using DocRAG.Core.Models;
using DocRAG.Database.Repositories;
using ModelContextProtocol.Server;

#endregion

namespace DocRAG.Mcp.Tools;

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
                 "distribution, language mix, boundary-issue rate, and suspect markers. " +
                 "For the actual library content, use get_library_overview instead."
                )]
    public static async Task<string> GetLibraryHealth(RepositoryFactory repositoryFactory,
                                                      [Description("Library identifier")]
                                                      string library,
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
            result = await BuildHealthResponseAsync(library, lib, version, chunkRepo, libraryRepo, ct);
        return result;
    }

    private static async Task<string> BuildHealthResponseAsync(
        string library,
        LibraryRecord lib,
        string? version,
        DocRAG.Core.Interfaces.IChunkRepository chunkRepo,
        DocRAG.Core.Interfaces.ILibraryRepository libraryRepo,
        CancellationToken ct)
    {
        var resolvedVersion = version ?? lib.CurrentVersion;
        var versionRecord = await libraryRepo.GetVersionAsync(library, resolvedVersion, ct);

        string result;
        if (versionRecord == null)
            result = JsonSerializer.Serialize(new { Error = $"Version '{resolvedVersion}' not found." }, smJsonOptions);
        else
            result = await BuildVersionSnapshotAsync(library, lib, resolvedVersion, versionRecord, chunkRepo, ct);
        return result;
    }

    private static async Task<string> BuildVersionSnapshotAsync(
        string library,
        LibraryRecord lib,
        string resolvedVersion,
        LibraryVersionRecord versionRecord,
        DocRAG.Core.Interfaces.IChunkRepository chunkRepo,
        CancellationToken ct)
    {
        var languageMix = await chunkRepo.GetLanguageMixAsync(library, resolvedVersion, ct);
        var hostnames = await chunkRepo.GetHostnameDistributionAsync(library, resolvedVersion, ct);
        var (boundaryHint, boundaryHintMessage) = ResolveBoundaryHint(versionRecord.BoundaryIssuePct);

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
                               boundaryHint = new { hint = boundaryHint, message = boundaryHintMessage }
                           };
        return JsonSerializer.Serialize(response, smJsonOptions);
    }

    private static (string? hint, string? message) ResolveBoundaryHint(double pct) => pct switch
    {
        >= BoundaryHintRecommendThreshold => (BoundaryHintRecommendedKey, BoundaryHintRecommendedMessage),
        >= BoundaryHintMayHelpThreshold => (BoundaryHintMayHelpKey, BoundaryHintMayHelpMessage),
        _ => (null, null)
    };

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };

    private const int MaxHostnamesReturned = 20;
    private const double BoundaryHintMayHelpThreshold = 5.0;
    private const double BoundaryHintRecommendThreshold = 10.0;
    private const string BoundaryHintRecommendedKey = "rechunk_recommended";
    private const string BoundaryHintRecommendedMessage = "rechunk_library recommended";
    private const string BoundaryHintMayHelpKey = "rechunk_may_help";
    private const string BoundaryHintMayHelpMessage = "rechunk_library may help";
}
