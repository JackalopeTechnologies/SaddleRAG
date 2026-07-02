// MonitorConfigCrash.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Compact crash-history card for the Monitor /config page (issue #140):
///     when the host last crashed (newest of the .NET Runtime crash event and
///     the newest crash dump), how many dumps are on disk, the newest dump's
///     file name, and whether a managed last-crash marker exists. The full
///     evidence set comes from the <c>get_crash_report</c> MCP tool.
/// </summary>
public sealed record MonitorConfigCrash(DateTime? LastCrashUtc,
                                        int CrashDumpCount,
                                        string? LastDumpFileName,
                                        bool HasManagedCrashMarker);
