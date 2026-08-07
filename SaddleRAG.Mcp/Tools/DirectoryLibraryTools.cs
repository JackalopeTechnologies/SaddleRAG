// DirectoryLibraryTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Mcp.Tools;

/// <summary>Explicit registration and manual-scan entry points for local directories.</summary>
[McpServerToolType]
public static class DirectoryLibraryTools
{
    [McpServerTool(Name = "register_directory_library")]
    [Description("Register a user-selected local directory as a SaddleRAG library. " +
                 "Registration validates and stores the path and scan options but never scans it. " +
                 "SaddleRAG does not watch the folder or schedule automatic rescans.")]
    public static async Task<string> RegisterDirectoryLibrary(
        IDirectoryLibraryRegistrationService service,
        [Description("Library identifier for the registered directory.")]
        string library,
        [Description("Explicit absolute directory path selected by the user.")]
        string path,
        [Description("Whether manual scans include subdirectories.")]
        bool recursive = true,
        [Description("Optional database profile name.")]
        string? profile = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(path);
        DirectoryRegistrationResult result = await service.RegisterAsync(
                                                 new DirectoryRegistrationRequest(library,
                                                                                  path,
                                                                                  recursive,
                                                                                  Array.Empty<string>()),
                                                 profile,
                                                 ct);
        return JsonSerializer.Serialize(result, smJsonOptions);
    }

    [McpServerTool(Name = "scan_directory_library")]
    [Description("Queue one manual scan of an already registered directory library. " +
                 "Returns a job id for get_job_status. This tool is the only runtime trigger; " +
                 "service startup and folder changes never start scans.")]
    public static async Task<string> ScanDirectoryLibrary(
        IDirectoryScanJobQueue queue,
        [Description("Previously registered directory-library identifier.")]
        string library,
        [Description("Optional database profile name.")]
        string? profile = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentException.ThrowIfNullOrEmpty(library);
        DirectoryScanQueueResult result = await queue.QueueAsync(library, profile, ct);
        return JsonSerializer.Serialize(result, smJsonOptions);
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
