// CrashReport.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     Aggregated crash evidence returned by the <c>get_crash_report</c>
///     MCP tool (issue #140). Every member is best-effort: absent evidence
///     is null or empty, never an error.
/// </summary>
public sealed record CrashReport(CrashEventInfo? LastRuntimeCrash,
                                 CrashEventInfo? LastFault,
                                 CrashEventInfo? LastServiceTermination,
                                 IReadOnlyList<WerReportInfo> WerReports,
                                 IReadOnlyList<CrashDumpInfo> CrashDumps,
                                 string? LastManagedCrash,
                                 IReadOnlyList<string> LogTail);
