// PlaywrightRuntimeProbeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

#region Usings

using Microsoft.Playwright;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class PlaywrightRuntimeProbeTests
{
    [Fact]
    public void PowerShellExecutableUsesWindowsPowerShellOnWindows()
    {
        string result = PlaywrightRuntimeProbe.GetPowerShellExecutable(isWindows: true);

        Assert.Equal("powershell.exe", result);
    }

    [Fact]
    public void PowerShellExecutableUsesPowerShellCoreOutsideWindows()
    {
        string result = PlaywrightRuntimeProbe.GetPowerShellExecutable(isWindows: false);

        Assert.Equal("pwsh", result);
    }

    [Fact]
    public void IsBrowserMissingReturnsTrueForMissingExecutable()
    {
        var exception = new PlaywrightException("Executable doesn't exist at C:\\cache\\chrome.exe");

        bool result = PlaywrightRuntimeProbe.IsBrowserMissing(exception);

        Assert.True(result);
    }

    [Fact]
    public void IsBrowserMissingReturnsFalseForUnrelatedLaunchFailure()
    {
        var exception = new PlaywrightException("Browser closed unexpectedly.");

        bool result = PlaywrightRuntimeProbe.IsBrowserMissing(exception);

        Assert.False(result);
    }
}