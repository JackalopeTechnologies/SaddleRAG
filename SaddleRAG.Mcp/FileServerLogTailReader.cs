// FileServerLogTailReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>
///     <see cref="IServerLogReader" /> over the newest saddlerag-*.log file.
///     Reads at most the trailing <see cref="TailWindowBytes" /> of the file
///     (shared read; Serilog keeps writing), drops the torn first line when
///     the window clipped, and parses with <see cref="ServerLogParser" />.
///     IO failures propagate to the caller per the interface contract.
/// </summary>
public sealed class FileServerLogTailReader : IServerLogReader
{
    public FileServerLogTailReader(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDirectory);
        mLogDirectory = logDirectory;
    }

    /// <inheritdoc />
    public ServerLogSnapshot Read(int maxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, other: 1);

        ServerLogSnapshot res = ServerLogSnapshot.Empty;
        string? logFile = ServerLogFiles.FindLatest(mLogDirectory);
        if (logFile != null)
        {
            (IReadOnlyList<string> lines, bool clipped) = ReadTailLines(logFile);
            IReadOnlyList<ServerLogEntry> entries = ServerLogParser.Parse(lines);

            res = new ServerLogSnapshot
                      {
                          LogFileName = Path.GetFileName(logFile),
                          Entries = entries.TakeLast(maxEntries).Reverse().ToList(),
                          TotalEntriesInWindow = entries.Count,
                          TruncatedAtWindow = clipped
                      };
        }

        return res;
    }

    /// <inheritdoc />
    public int CountRecentErrors(TimeSpan window)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - window;
        ServerLogSnapshot snapshot = Read(int.MaxValue);

        return snapshot.Entries.Count(e => e.Level >= ServerLogLevel.Error && e.Timestamp >= cutoff);
    }

    private static (IReadOnlyList<string> Lines, bool Clipped) ReadTailLines(string logFile)
    {
        using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        bool clipped = stream.Length > TailWindowBytes;
        if (clipped)
            stream.Seek(-TailWindowBytes, SeekOrigin.End);

        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string? line = reader.ReadLine();
        var first = true;
        while (line != null)
        {
            bool tornHead = first && clipped;
            if (!tornHead)
                lines.Add(line);
            first = false;
            line = reader.ReadLine();
        }

        return (lines, clipped);
    }

    private readonly string mLogDirectory;

    private const int TailWindowBytes = 512 * 1024;
}
