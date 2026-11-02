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
///     Starts its own child processes to stop. It never targets a process it did not start: the
///     access-denied-by-Windows and timed-out outcomes would need a process the test must not
///     touch, so they are not exercised here.
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
        return Process.Start(startInfo) ?? throw new InvalidOperationException("ping did not start");
    }

    /// <summary>
    ///     Starts a PowerShell child that itself starts a ping and prints its process id, so the test
    ///     has a parent and a grandchild it started, and knows both ids.
    /// </summary>
    private static async Task<(Process Parent, Process Grandchild)> StartParentWithGrandchildAsync(CancellationToken ct)
    {
        string powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powerShell)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true
                        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("$p = Start-Process -FilePath ping -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden -PassThru; $p.Id; Start-Sleep -Seconds 120");
        Process parent = Process.Start(startInfo) ?? throw new InvalidOperationException("powershell did not start");
        try
        {
            string? line = await parent.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(seconds: 30), ct);
            return (parent, Process.GetProcessById(int.Parse(line!.Trim())));
        }
        catch
        {
            KillIfRunning(parent);
            parent.Dispose();
            throw;
        }
    }

    private static void KillIfRunning(Process process)
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }

    private static ListeningProcess Describe(Process process) =>
        new(process.Id, process.ProcessName, ExecutablePath: null, process.StartTime.ToUniversalTime());

    [Fact]
    public async Task StopsAChildProcessTheTestStarted()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        using Process child = StartLongRunningChild();
        try
        {
            ProcessStopOutcome outcome = await new UserProcessStopper().StopAsync(Describe(child), entireProcessTree: true, smStopTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(ProcessStopOutcome.Stopped, outcome);
            Assert.True(child.HasExited);
        }
        finally
        {
            KillIfRunning(child);
        }
    }

    [Fact]
    public async Task ReportsNotFoundForAProcessIdNoProcessCanHave()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        // Windows process ids are multiples of four, so an odd id can never be a running process.
        var gone = new ListeningProcess(int.MaxValue, "gone", ExecutablePath: null, DateTime.UtcNow);

        ProcessStopOutcome outcome = await new UserProcessStopper().StopAsync(gone, entireProcessTree: true, smStopTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessStopOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task LeavesAProcessAloneWhenItsStartTimeIsNotTheOneShown()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        // The same id, but a different start time: what a reused process id looks like.
        using Process child = StartLongRunningChild();
        try
        {
            ListeningProcess shown = Describe(child) with { StartTimeUtc = child.StartTime.ToUniversalTime().AddSeconds(-30) };

            ProcessStopOutcome outcome = await new UserProcessStopper().StopAsync(shown, entireProcessTree: true, smStopTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(ProcessStopOutcome.NotFound, outcome);
            Assert.False(child.HasExited);
        }
        finally
        {
            KillIfRunning(child);
        }
    }

    [Fact]
    public async Task DoesNotTouchAProcessWhoseStartTimeIsUnknown()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        using Process child = StartLongRunningChild();
        try
        {
            ListeningProcess shown = Describe(child) with { StartTimeUtc = null };

            ProcessStopOutcome outcome = await new UserProcessStopper().StopAsync(shown, entireProcessTree: true, smStopTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(ProcessStopOutcome.AccessDenied, outcome);
            Assert.False(child.HasExited);
        }
        finally
        {
            KillIfRunning(child);
        }
    }

    [Fact]
    public async Task StopsOnlyTheTargetWhenTheProcessTreeIsNotRequested()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        (Process parent, Process grandchild) = await StartParentWithGrandchildAsync(TestContext.Current.CancellationToken);
        using Process parentProcess = parent;
        using Process grandchildProcess = grandchild;
        try
        {
            ProcessStopOutcome outcome = await new UserProcessStopper().StopAsync(Describe(parentProcess), entireProcessTree: false, smStopTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(ProcessStopOutcome.Stopped, outcome);
            Assert.True(parentProcess.HasExited);
            Assert.False(grandchildProcess.HasExited);
        }
        finally
        {
            KillIfRunning(grandchildProcess);
            KillIfRunning(parentProcess);
        }
    }

    [Fact]
    public async Task StopsTheTargetAndWhatItStartedWhenTheProcessTreeIsRequested()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        (Process parent, Process grandchild) = await StartParentWithGrandchildAsync(TestContext.Current.CancellationToken);
        using Process parentProcess = parent;
        using Process grandchildProcess = grandchild;
        try
        {
            ProcessStopOutcome outcome = await new UserProcessStopper().StopAsync(Describe(parentProcess), entireProcessTree: true, smStopTimeout, TestContext.Current.CancellationToken);

            Assert.Equal(ProcessStopOutcome.Stopped, outcome);
            Assert.True(parentProcess.HasExited);
            Assert.True(grandchildProcess.WaitForExit(smStopTimeout));
        }
        finally
        {
            KillIfRunning(grandchildProcess);
            KillIfRunning(parentProcess);
        }
    }

    [Fact]
    public async Task RefusesToStopItsOwnProcess()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        using Process self = Process.GetCurrentProcess();

        await Assert.ThrowsAsync<ArgumentException>(() => new UserProcessStopper().StopAsync(Describe(self), entireProcessTree: true, smStopTimeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefusesANullProcess()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        await Assert.ThrowsAsync<ArgumentNullException>(() => new UserProcessStopper().StopAsync(null!, entireProcessTree: true, smStopTimeout, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-4, 10)]
    [InlineData(100, 0)]
    public async Task RefusesAnInvalidProcessIdOrTimeout(int processId, int timeoutSeconds)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        var shown = new ListeningProcess(processId, "invalid", ExecutablePath: null, DateTime.UtcNow);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new UserProcessStopper().StopAsync(shown, entireProcessTree: true, TimeSpan.FromSeconds(timeoutSeconds), TestContext.Current.CancellationToken));
    }
}