// LogDirectoryResolverTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Locks in where the MCP host writes its Serilog file logs (issue #138).
///     Service mode must use CommonApplicationData (ProgramData): a LocalSystem
///     service's LocalApplicationData is the admin-only systemprofile hive,
///     which made the 2026-07-02 crash post-mortem needlessly painful.
///     Interactive runs keep the per-user LocalApplicationData location.
/// </summary>
public sealed class LogDirectoryResolverTests
{
    [Fact]
    public void ServiceModeResolvesToProgramData()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                       "SaddleRAG",
                                       "logs");

        Assert.Equal(expected, LogDirectoryResolver.Resolve(isWindowsService: true));
    }

    [Fact]
    public void InteractiveModeResolvesToLocalApplicationData()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "SaddleRAG",
                                       "logs");

        Assert.Equal(expected, LogDirectoryResolver.Resolve(isWindowsService: false));
    }
}
