// UserProcessStopperTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;
using System.Runtime.Versioning;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

/// <summary>
///     Starts its own child process to stop. It never targets a process it did not start: the
///     access-denied and timed-out outcomes would need a process the test must not touch, so they
///     are not exercised here.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UserProcessStopperTests
{
    private const string WindowsOnlySkipReason = "Stopping a process tree is exercised on Windows only.";
    private static readonly TimeSpan smStopTimeout = TimeSpan.FromSeconds(seconds: 10);

    private static Process StartLongRunningChild()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        var startInfo = new ProcessStartInfo(ping, "-n 60 127.0.0.1")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true
                        };
        return Process.Start(startInfo)!;
    }

    [Fact]
    public async Task StopsAChildProcessTheTestStarted()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        using Process child = StartLongRunningChild();
        try
        {
            ProcessStopOutcome outcome = await new UserProcessStopper().StopAsync(child.Id, smStopTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(ProcessStopOutcome.Stopped, outcome);
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task ReportsNotFoundForAProcessIdNoProcessCanHave()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        // Windows process ids are multiples of four, so an odd id can never be a running process.
        ProcessStopOutcome outcome = await new UserProcessStopper().StopAsync(int.MaxValue, smStopTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessStopOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task RefusesToStopItsOwnProcess()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        await Assert.ThrowsAsync<ArgumentException>(() => new UserProcessStopper().StopAsync(Environment.ProcessId, smStopTimeout, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-4, 10)]
    [InlineData(100, 0)]
    public async Task RefusesAnInvalidProcessIdOrTimeout(int processId, int timeoutSeconds)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new UserProcessStopper().StopAsync(processId, TimeSpan.FromSeconds(timeoutSeconds), TestContext.Current.CancellationToken));
    }
}