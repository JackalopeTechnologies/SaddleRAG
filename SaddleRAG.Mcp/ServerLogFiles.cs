// ServerLogFiles.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp;

/// <summary>
///     Shared primitives for locating and reading the Serilog log files, used
///     by both the get_server_logs MCP tool and the monitor's tail reader so
///     the two surfaces cannot drift (issue #143).
/// </summary>
public static class ServerLogFiles
{
    /// <summary>
    ///     Pick the most recently written log file. Sorted by
    ///     <see cref="FileSystemInfo.LastWriteTimeUtc" /> rather than filename
    ///     so the picker is robust against rotation schemes that don't sort
    ///     lexicographically.
    /// </summary>
    public static string? FindLatest(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDirectory);

        string? result = null;
        if (Directory.Exists(logDirectory))
        {
            result = new DirectoryInfo(logDirectory)
                     .EnumerateFiles(SearchPattern)
                     .OrderByDescending(f => f.LastWriteTimeUtc)
                     .FirstOrDefault()
                     ?.FullName;
        }

        return result;
    }

    /// <summary>
    ///     Read every line of a log file that Serilog may still be writing
    ///     (FileShare.ReadWrite matches the sink's shared: true).
    /// </summary>
    public static IReadOnlyList<string> ReadAllLinesShared(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        string? line = reader.ReadLine();
        while (line != null)
        {
            lines.Add(line);
            line = reader.ReadLine();
        }

        return lines;
    }

    public const string SearchPattern = "saddlerag-*.log";
}
