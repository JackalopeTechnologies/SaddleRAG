// LogNotificationReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

namespace SaddleRAG.Mcp;

/// <summary>Reads log notification counts without touching MongoDB.</summary>
public sealed class LogNotificationReader
{
    private const int MaximumEntries = 2000;
    private readonly Lock mGate = new();
    private readonly IServerLogReader mReader;
    private readonly IServerLogAcknowledgement mAcknowledgement;
    private DateTimeOffset? mLastWarning;
    private DateTimeOffset? mLastError;

    public LogNotificationReader(IServerLogReader reader, IServerLogAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        mReader = reader;
        mAcknowledgement = acknowledgement;
    }

    public LogNotificationStatus Read()
    {
        LogNotificationStatus result;
        lock (mGate)
            result = ReadLocked();
        return result;
    }

    private LogNotificationStatus ReadLocked()
    {
        LogNotificationStatus result;
        DateTimeOffset acknowledged = mAcknowledgement.AcknowledgedThrough ?? DateTimeOffset.MinValue;
        try
        {
            var entries = mReader.Read(MaximumEntries).Entries
                .Where(entry => entry.Level >= ServerLogLevel.Warning
                    && entry.Timestamp > acknowledged).ToArray();
            // Retain only timestamps, not an unbounded queue of log messages. An
            // observed problem survives log-tail clipping and rotation until read.
            foreach (var entry in entries)
            {
                if (entry.Level == ServerLogLevel.Warning && (mLastWarning == null || entry.Timestamp > mLastWarning))
                    mLastWarning = entry.Timestamp;
                if (entry.Level >= ServerLogLevel.Error && (mLastError == null || entry.Timestamp > mLastError))
                    mLastError = entry.Timestamp;
            }
            DateTimeOffset? newest = new[] { mLastWarning, mLastError }.Max();
            result = new(entries.Count(entry => entry.Level == ServerLogLevel.Warning),
                         entries.Count(entry => entry.Level >= ServerLogLevel.Error),
                         newest > acknowledged ? newest : null, false)
            {
                HasWarnings = mLastWarning > acknowledged,
                HasErrors = mLastError > acknowledged
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = new(0, 0, null, true);
        }
        return result;
    }
}
