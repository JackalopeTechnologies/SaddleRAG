// WindowsEventLogCrashReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;

#endregion

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     Real <see cref="ICrashEventReader" /> over the Windows event log.
///     Reads the newest matching record via reverse-direction XPath queries:
///     .NET Runtime 1026 and Application Error 1000 from the Application log
///     (filtered to mentions of the MCP host executable, since other
///     applications share those providers), and Service Control Manager
///     7031/7034 from the System log (filtered to the SaddleRAG service).
///     Best-effort by contract: any failure — non-Windows host, missing log,
///     access denied — yields null rather than an exception, because the
///     crash report must degrade gracefully on a machine with no evidence.
/// </summary>
public sealed class WindowsEventLogCrashReader : ICrashEventReader
{
    /// <inheritdoc />
    public CrashEventInfo? ReadLastRuntimeCrash() =>
        ReadNewestMatching(ApplicationLogName, RuntimeCrashQuery, ProcessNameFilter);

    /// <inheritdoc />
    public CrashEventInfo? ReadLastFault() =>
        ReadNewestMatching(ApplicationLogName, FaultQuery, ProcessNameFilter);

    /// <inheritdoc />
    public CrashEventInfo? ReadLastServiceTermination() =>
        ReadNewestMatching(SystemLogName, ServiceTerminationQuery, ServiceNameFilter);

    private static CrashEventInfo? ReadNewestMatching(string logName, string xpath, string messageFilter)
    {
        CrashEventInfo? result = null;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                result = QueryNewestMatching(logName, xpath, messageFilter);
            }
            catch(Exception ex) when(ex is EventLogException or UnauthorizedAccessException)
            {
                result = null;
            }
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static CrashEventInfo? QueryNewestMatching(string logName, string xpath, string messageFilter)
    {
        var query = new EventLogQuery(logName, PathType.LogName, xpath)
                        {
                            ReverseDirection = true
                        };
        using var reader = new EventLogReader(query);

        CrashEventInfo? result = null;
        EventRecord? record = reader.ReadEvent();

        while (record != null && result == null)
        {
            result = ExtractIfMatching(record, messageFilter);

            if (result == null)
                record = reader.ReadEvent();
        }

        return result;
    }

    [SupportedOSPlatform("windows")]
    private static CrashEventInfo? ExtractIfMatching(EventRecord record, string messageFilter)
    {
        CrashEventInfo? result = null;

        using (record)
        {
            string message = record.FormatDescription() ?? string.Empty;

            if (message.Contains(messageFilter, StringComparison.OrdinalIgnoreCase))
            {
                DateTime timeUtc = record.TimeCreated?.ToUniversalTime() ?? DateTime.MinValue;
                result = new CrashEventInfo(timeUtc, message);
            }
        }

        return result;
    }

    private const string ApplicationLogName = "Application";
    private const string SystemLogName = "System";
    private const string ProcessNameFilter = "SaddleRAG.Mcp";
    private const string ServiceNameFilter = "SaddleRAG";

    private const string RuntimeCrashQuery =
        "*[System[Provider[@Name='.NET Runtime'] and EventID=1026]]";

    private const string FaultQuery =
        "*[System[Provider[@Name='Application Error'] and EventID=1000]]";

    private const string ServiceTerminationQuery =
        "*[System[Provider[@Name='Service Control Manager'] and (EventID=7031 or EventID=7034)]]";
}
