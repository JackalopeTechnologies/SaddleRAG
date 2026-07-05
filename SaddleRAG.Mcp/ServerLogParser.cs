// ServerLogParser.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Globalization;
using System.Text.RegularExpressions;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>
///     Parses Serilog default-template file output into
///     <see cref="ServerLogEntry" /> records. A line beginning with
///     "yyyy-MM-dd HH:mm:ss.fff zzz [LVL] " starts a new entry; every other
///     line is a continuation (exception/stack) of the preceding entry.
///     Leading continuation lines with no parent entry are dropped — they are
///     the torn head of a tail window.
/// </summary>
public static partial class ServerLogParser
{
    /// <summary>
    ///     Parse raw log lines into entries, in chronological (file) order.
    /// </summary>
    public static IReadOnlyList<ServerLogEntry> Parse(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entries = new List<ServerLogEntry>();
        DateTimeOffset currentTimestamp = default;
        var currentLevel = ServerLogLevel.Information;
        string? currentMessage = null;
        var currentDetail = new List<string>();

        foreach (string line in lines)
        {
            Match match = EntryStartRegex().Match(line);
            DateTimeOffset timestamp = default;
            bool startsEntry = match.Success &&
                               DateTimeOffset.TryParseExact(match.Groups[TimestampGroup].Value,
                                                            TimestampFormat,
                                                            CultureInfo.InvariantCulture,
                                                            DateTimeStyles.None,
                                                            out timestamp
                                                           );

            if (startsEntry)
            {
                if (currentMessage != null)
                    entries.Add(BuildEntry(currentTimestamp, currentLevel, currentMessage, currentDetail));

                currentTimestamp = timestamp;
                currentLevel = ParseLevel(match.Groups[LevelGroup].Value);
                currentMessage = match.Groups[MessageGroup].Value;
                currentDetail = [];
            }
            else
            {
                if (currentMessage != null)
                    currentDetail.Add(line);
            }
        }

        if (currentMessage != null)
            entries.Add(BuildEntry(currentTimestamp, currentLevel, currentMessage, currentDetail));

        return entries;
    }

    private static ServerLogEntry BuildEntry(DateTimeOffset timestamp,
                                             ServerLogLevel level,
                                             string message,
                                             List<string> detail)
    {
        return new ServerLogEntry
                   {
                       Timestamp = timestamp,
                       Level = level,
                       Message = message,
                       DetailLines = detail
                   };
    }

    private static ServerLogLevel ParseLevel(string token) => token switch
                                                              {
                                                                  "VRB" => ServerLogLevel.Verbose,
                                                                  "DBG" => ServerLogLevel.Debug,
                                                                  "INF" => ServerLogLevel.Information,
                                                                  "WRN" => ServerLogLevel.Warning,
                                                                  "ERR" => ServerLogLevel.Error,
                                                                  "FTL" => ServerLogLevel.Fatal,
                                                                  _ => ServerLogLevel.Information
                                                              };

    [GeneratedRegex(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2}) " +
                    @"\[(?<lvl>VRB|DBG|INF|WRN|ERR|FTL)\] (?<msg>.*)$"
                   )]
    private static partial Regex EntryStartRegex();

    private const string TimestampGroup = "ts";
    private const string LevelGroup = "lvl";
    private const string MessageGroup = "msg";
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";
}
