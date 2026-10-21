// LogNotificationReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

namespace SaddleRAG.Mcp;

/// <summary>Reads log notification counts without touching MongoDB.</summary>
public static class LogNotificationReader
{
    private const int MaximumEntries = 2000;

    public static LogNotificationStatus Read(IServerLogReader reader, IServerLogAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        LogNotificationStatus result;
        try
        {
            var entries = reader.Read(MaximumEntries).Entries
                .Where(entry => entry.Level >= ServerLogLevel.Warning
                    && (acknowledgement.AcknowledgedThrough == null
                        || entry.Timestamp > acknowledgement.AcknowledgedThrough)).ToArray();
            result = new(entries.Count(entry => entry.Level == ServerLogLevel.Warning),
                         entries.Count(entry => entry.Level >= ServerLogLevel.Error),
                         entries.Select(entry => (DateTimeOffset?)entry.Timestamp).Max(), false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = new(0, 0, null, true);
        }
        return result;
    }
}
