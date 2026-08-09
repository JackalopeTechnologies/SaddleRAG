// PackagingTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Packaging;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     MCP tools for exporting and importing SaddleRAG library bundles.
///     Export streams an indexed library (one or more versions) into a
///     single .srlib.zip file. Import reads that bundle and writes it
///     into the receiver's MongoDB, enqueuing a re-embed job when the
///     encoder fingerprints differ.
/// </summary>
[McpServerToolType]
public static class PackagingTools
{
    [McpServerTool(Name = "export_library")]
    [Description("Export an indexed library (one or more versions) as a single .srlib.zip file. " +
                 "The bundle carries pages, chunks (with embeddings), BM25 index, profile, and curation " +
                 "so a receiver can import it and query immediately when their embedding model matches. " +
                 "On encoder mismatch, the receiver re-embeds from the included chunk text via the existing job queue.")]
    public static async Task<string> ExportLibrary(LibraryExporter exporter,
                                                   [Description("Library identifier")]
                                                   string library,
                                                   [Description("Version selector: 'current' (default), 'all', or a JSON array of version strings like [\"1.0\",\"1.1\"]")]
                                                   string? versions = null,
                                                   [Description("Output file path. Defaults to './{library}.srlib.zip' in the working directory.")]
                                                   string? outputPath = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentException.ThrowIfNullOrEmpty(library);

        var filter = ParseVersions(versions);
        var resolvedOutput = outputPath ?? $"{library}.srlib.zip";

        var request = new ExportRequest
                          {
                              LibraryId = library,
                              Versions = filter,
                              OutputPath = resolvedOutput
                          };

        var result = await exporter.ExportAsync(request, progress: null, ct: ct);
        return JsonSerializer.Serialize(result, smJsonOptions);
    }

    [McpServerTool(Name = "import_library")]
    [Description("Import a SaddleRAG library bundle (.srlib.zip) produced by export_library. " +
                 "Refuses by default if any target (library, version) already exists on the receiver; " +
                 "pass overwrite=true to replace. If the bundle's embedding model doesn't match the " +
                 "receiver's active model, chunks land with null embeddings and a re-embed job is " +
                 "enqueued per imported version. Pass compact=true to run compact_collections " +
                 "automatically after a successful overwrite-import.")]
    public static async Task<string> ImportLibrary(LibraryImporterFactory importerFactory,
                                                   RepositoryFactory repositoryFactory,
                                                   ScrapeJobRunner runner,
                                                   ILogger<PackagingToolsLog> logger,
                                                   [Description("Path to a .srlib.zip bundle on disk.")]
                                                   string bundlePath,
                                                   [Description("If true, replace existing (library, version) targets. Default false (refuse on conflict).")]
                                                   bool overwrite = false,
                                                   [Description("If true, run compact_collections after a successful overwrite-import. Default false.")]
                                                   bool compact = false,
                                                   [Description("Optional database profile name (use the same as compact_collections)")]
                                                   string? profile = null,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(importerFactory);
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(bundlePath);

        LibraryImporter importer = importerFactory.Create(profile);
        var result = await importer.ImportAsync(
            new ImportRequest { BundlePath = bundlePath, Overwrite = overwrite, Compact = compact, Profile = profile },
            progress: null,
            ct: ct);

        result = await ReloadImportedLibraryAsync(repositoryFactory,
                                                  runner,
                                                  logger,
                                                  result,
                                                  profile,
                                                  bundlePath,
                                                  ct);
        return JsonSerializer.Serialize(result, smJsonOptions);
    }

    // An import writes chunks and embeddings straight into MongoDB but, unlike scrape
    // ingestion, does not refresh the in-memory vector index — so a freshly imported library
    // returns zero search candidates until a reload or restart. Reload only matching-encoder
    // versions. A version with a durable re-embed job is owned by ReembedJobRunner, including
    // when that job completes before this method runs; reloading the importer's null embeddings
    // would otherwise replace the fresh index with an empty one. The data was already committed,
    // so a reload failure is reported via RecommendedFollowUp rather than surfaced as an import
    // failure; cancellation still propagates.
    internal static async Task<ImportResult> ReloadImportedLibraryAsync(
        RepositoryFactory repositoryFactory,
        ScrapeJobRunner runner,
        ILogger<PackagingToolsLog> logger,
        ImportResult result,
        string? profile,
        string bundlePath,
        CancellationToken ct)
    {
        ImportResult res = result;
        List<string> versions = result.VersionsImported
                                      .Concat(result.OverwrittenVersions)
                                      .Distinct(StringComparer.Ordinal)
                                      .ToList();
        if (versions.Count > 0)
        {
            try
            {
                IReadOnlySet<string> reembedVersions = await GetDurableReembedVersionsAsync(
                                                            repositoryFactory.GetJobRepository(profile),
                                                            result,
                                                            versions,
                                                            profile,
                                                            ct);
                await ReloadVersionsWithoutReembedJobsAsync(runner,
                                                            result.LibraryId,
                                                            versions,
                                                            reembedVersions,
                                                            profile,
                                                            ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, ReloadFailedLogMessage, bundlePath, result.LibraryId);
                res = result with { RecommendedFollowUp = AppendReloadWarning(result.RecommendedFollowUp) };
            }
        }
        return res;
    }

    private static async Task ReloadVersionsWithoutReembedJobsAsync(
        ScrapeJobRunner runner,
        string libraryId,
        IReadOnlyList<string> versions,
        IReadOnlySet<string> reembedVersions,
        string? profile,
        CancellationToken ct)
    {
        foreach(string version in versions)
        {
            if (!reembedVersions.Contains(version))
                await runner.ReloadIndexForLibraryAsync(profile, libraryId, version, ct);
        }
    }

    private static async Task<IReadOnlySet<string>> GetDurableReembedVersionsAsync(
        IJobRepository jobs,
        ImportResult import,
        IReadOnlyList<string> importedVersions,
        string? profile,
        CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach(string jobId in import.PendingReembedJobIds.Distinct(StringComparer.Ordinal))
        {
            JobRecord? job = await jobs.GetAsync(jobId, ct);
            if (job == null ||
                !string.Equals(job.Id, jobId, StringComparison.Ordinal) ||
                job.JobType != JobType.Reembed ||
                !string.Equals(job.Profile, profile, StringComparison.Ordinal) ||
                !string.Equals(job.LibraryId, import.LibraryId, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(job.Version) ||
                !importedVersions.Contains(job.Version, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending re-embed job '{jobId}' could not be attributed to this import.");
            }

            result.Add(job.Version);
        }
        return result;
    }

    private static string AppendReloadWarning(string existing) =>
        string.IsNullOrEmpty(existing) ? ReloadWarningFollowUp : $"{existing} {ReloadWarningFollowUp}";

    private static VersionFilter ParseVersions(string? versions)
    {
        VersionFilter result;
        if (string.IsNullOrWhiteSpace(versions))
        {
            result = VersionFilter.Current;
        }
        else
        {
            var trimmed = versions.Trim();
            if (trimmed.StartsWith('['))
            {
                var list = JsonSerializer.Deserialize<List<string>>(trimmed)
                           ?? throw new ArgumentException($"Could not parse versions JSON array: {versions}",
                                                          nameof(versions));
                result = VersionFilter.Parse(list);
            }
            else
            {
                result = VersionFilter.Parse(trimmed);
            }
        }
        return result;
    }

    private const string ReloadFailedLogMessage =
        "Imported {Bundle} ({Library}) but the vector-index reload failed; run reload_profile or restart to search it.";

    private const string ReloadWarningFollowUp =
        "WARNING: import succeeded but the vector-index reload failed; run reload_profile or restart to make searchable.";

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };

    /// <summary>
    ///     Logger category marker for <see cref="PackagingTools" /> — a static class cannot be the
    ///     <c>T</c> in <c>ILogger&lt;T&gt;</c>, so the DI-injected logger uses this type instead.
    /// </summary>
    public sealed class PackagingToolsLog
    {
    }
}
