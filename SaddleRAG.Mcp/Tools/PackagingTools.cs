// PackagingTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
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
    public static async Task<string> ImportLibrary(LibraryImporter importer,
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
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentException.ThrowIfNullOrEmpty(bundlePath);

        var result = await importer.ImportAsync(
            new ImportRequest { BundlePath = bundlePath, Overwrite = overwrite, Compact = compact, Profile = profile },
            progress: null,
            ct: ct);

        return JsonSerializer.Serialize(result, smJsonOptions);
    }

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

    private static readonly JsonSerializerOptions smJsonOptions = new JsonSerializerOptions { WriteIndented = true };
}
