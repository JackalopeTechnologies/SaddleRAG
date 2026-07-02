// ICrashEventReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     Seam over the Windows event log for <see cref="CrashReportService" />
///     (issue #140). The real implementation
///     (<see cref="WindowsEventLogCrashReader" />) queries the Application and
///     System logs; tests substitute a fake. All reads are best-effort — a
///     missing log, denied access, or non-Windows host yields null, never an
///     exception.
/// </summary>
public interface ICrashEventReader
{
    /// <summary>
    ///     Latest .NET Runtime event 1026 (unhandled exception, includes the
    ///     managed stack) for the MCP host process, or null.
    /// </summary>
    CrashEventInfo? ReadLastRuntimeCrash();

    /// <summary>
    ///     Latest Application Error event 1000 (faulting module + exception
    ///     code) for the MCP host process, or null.
    /// </summary>
    CrashEventInfo? ReadLastFault();

    /// <summary>
    ///     Latest Service Control Manager 7031/7034 (service terminated
    ///     unexpectedly) event for the SaddleRAG service, or null.
    /// </summary>
    CrashEventInfo? ReadLastServiceTermination();
}
