// ServerLogFilter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Pure filtering used by the Logs page: level threshold plus a
///     case-insensitive contains-match over message and detail lines.
/// </summary>
public static class ServerLogFilter
{
    public static IReadOnlyList<ServerLogEntry> Apply(IReadOnlyList<ServerLogEntry> entries,
                                                      ServerLogLevelFilter levelFilter,
                                                      string? text)
    {
        ArgumentNullException.ThrowIfNull(entries);

        IEnumerable<ServerLogEntry> res = levelFilter switch
                                          {
                                              ServerLogLevelFilter.WarningsPlus =>
                                                  entries.Where(e => e.Level >= ServerLogLevel.Warning),
                                              ServerLogLevelFilter.ErrorsOnly =>
                                                  entries.Where(e => e.Level >= ServerLogLevel.Error),
                                              _ => entries
                                          };

        if (!string.IsNullOrWhiteSpace(text))
        {
            res = res.Where(e => e.Message.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                 e.DetailLines.Any(d => d.Contains(text, StringComparison.OrdinalIgnoreCase))
                          );
        }

        return res.ToList();
    }
}
