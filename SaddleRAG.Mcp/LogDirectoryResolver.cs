// LogDirectoryResolver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp;

/// <summary>
///     Resolves where the MCP host writes its Serilog file logs (issue #138).
///     When running as a Windows service (LocalSystem), LocalApplicationData
///     is the admin-only <c>systemprofile</c> hive, which hid the service logs
///     during the 2026-07-02 crash post-mortem. Service mode therefore uses
///     CommonApplicationData (<c>%ProgramData%\SaddleRAG\logs</c>) — readable
///     without elevation and colocated with the CrashDumps folder — while
///     interactive runs keep the per-user LocalApplicationData location.
/// </summary>
public static class LogDirectoryResolver
{
    /// <summary>
    ///     Returns the log directory for the current hosting mode. Does not
    ///     create the directory; the caller owns that.
    /// </summary>
    /// <param name="isWindowsService">
    ///     Whether the process is hosted as a Windows service
    ///     (<c>WindowsServiceHelpers.IsWindowsService()</c>).
    /// </param>
    public static string Resolve(bool isWindowsService)
    {
        Environment.SpecialFolder root = isWindowsService
                                             ? Environment.SpecialFolder.CommonApplicationData
                                             : Environment.SpecialFolder.LocalApplicationData;

        return Path.Combine(Environment.GetFolderPath(root), AppName, LogSubdirectory);
    }

    private const string AppName = "SaddleRAG";
    private const string LogSubdirectory = "logs";
}
