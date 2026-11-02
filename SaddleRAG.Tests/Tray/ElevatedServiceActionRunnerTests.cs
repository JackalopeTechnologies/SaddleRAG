// ElevatedServiceActionRunnerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class ElevatedServiceActionRunnerTests
{
    private const int UacDeclinedErrorCode = 1223;
    private const int FileNotFoundErrorCode = 2;

    private static readonly string smScriptPath = Path.Combine("installed-scripts", "Restart-SaddleRAGComponent.ps1");

    private sealed class FakeLauncher : IElevatedProcessLauncher
    {
        public Func<ProcessStartInfo, CancellationToken, Task<int>> Behavior { get; set; } =
            (_, _) => Task.FromResult(0);

        public int Calls { get; private set; }
        public ProcessStartInfo? StartInfo { get; private set; }

        public Task<int> RunAsync(ProcessStartInfo startInfo, CancellationToken ct)
        {
            Calls++;
            StartInfo = startInfo;
            return Behavior(startInfo, ct);
        }
    }

    private sealed class FakeProbe : IFileSystemProbe
    {
        public bool ScriptExists { get; set; } = true;
        public int FileExistsCalls { get; private set; }

        public bool FileExists(string path)
        {
            FileExistsCalls++;
            return ScriptExists;
        }

        public bool DirectoryExists(string path) => false;

        public string? FindOnPath(string fileName) => null;
    }

    private sealed class FakeGuard : IInstallLocationGuard
    {
        public InstallLocationVerdict Verdict { get; set; } = InstallLocationVerdict.Safe;
        public int Calls { get; private set; }
        public string? CheckedPath { get; private set; }

        public InstallLocationVerdict Check(string scriptPath)
        {
            Calls++;
            CheckedPath = scriptPath;
            return Verdict;
        }
    }

    private sealed class ListLogger : ILogger<ElevatedServiceActionRunner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class Rig
    {
        public Rig(TimeSpan? waitTimeout = null)
        {
            Runner = new ElevatedServiceActionRunner(Launcher, smScriptPath, Probe, Guard, Logger, waitTimeout);
        }

        public FakeLauncher Launcher { get; } = new();
        public FakeProbe Probe { get; } = new();
        public FakeGuard Guard { get; } = new();
        public ListLogger Logger { get; } = new();
        public ElevatedServiceActionRunner Runner { get; }
    }

    [Fact]
    public async Task LaunchesWindowsPowerShellElevatedWithTheScriptAndArguments()
    {
        Rig rig = new();

        await rig.Runner.RunAsync(["MongoDB", "SaddleRAGMcp"], ServiceAction.Restart, TestContext.Current.CancellationToken);

        ProcessStartInfo info = Assert.IsType<ProcessStartInfo>(rig.Launcher.StartInfo);
        string expectedExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                          "WindowsPowerShell",
                                          "v1.0",
                                          "powershell.exe");
        Assert.Equal(expectedExe, info.FileName);
        Assert.Equal("runas", info.Verb);
        Assert.True(info.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.Contains($"-File \"{smScriptPath}\"", info.Arguments);
        Assert.Contains("-ServiceName MongoDB,SaddleRAGMcp", info.Arguments);
        Assert.Contains("-Action Restart", info.Arguments);
        Assert.Contains("-NoProfile", info.Arguments);
        Assert.Contains("-NonInteractive", info.Arguments);
        Assert.Contains("-ExecutionPolicy Bypass", info.Arguments);
        Assert.Contains("-TimeoutSeconds 120", info.Arguments);
    }

    [Theory]
    [InlineData(ServiceAction.Start, "-Action Start")]
    [InlineData(ServiceAction.Stop, "-Action Stop")]
    [InlineData(ServiceAction.Restart, "-Action Restart")]
    public async Task PassesTheRequestedActionToTheScript(ServiceAction action, string expected)
    {
        Rig rig = new();

        await rig.Runner.RunAsync(["SaddleRAGMcp"], action, TestContext.Current.CancellationToken);

        Assert.Contains(expected, rig.Launcher.StartInfo!.Arguments);
    }

    [Fact]
    public async Task KeepsServiceNamesInTheOrderGiven()
    {
        Rig rig = new();

        await rig.Runner.RunAsync(["SaddleRAGMcp", "MongoDB"], ServiceAction.Stop, TestContext.Current.CancellationToken);

        Assert.Contains("-ServiceName SaddleRAGMcp,MongoDB", rig.Launcher.StartInfo!.Arguments);
    }

    [Theory]
    [InlineData(0, ServiceActionOutcome.Succeeded)]
    [InlineData(10, ServiceActionOutcome.NotAllowed)]
    [InlineData(11, ServiceActionOutcome.NotInstalled)]
    [InlineData(12, ServiceActionOutcome.TimedOut)]
    [InlineData(13, ServiceActionOutcome.Failed)]
    [InlineData(1, ServiceActionOutcome.Failed)]
    [InlineData(-1, ServiceActionOutcome.Failed)]
    [InlineData(14, ServiceActionOutcome.Failed)]
    [InlineData(255, ServiceActionOutcome.Failed)]
    public async Task MapsScriptExitCodesToOutcomes(int exitCode, ServiceActionOutcome expected)
    {
        Rig rig = new();
        rig.Launcher.Behavior = (_, _) => Task.FromResult(exitCode);

        ServiceActionOutcome outcome = await rig.Runner.RunAsync(["SaddleRAGMcp"],
                                                                 ServiceAction.Start,
                                                                 TestContext.Current.CancellationToken);

        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task ADeclinedPromptIsCancelledAndLogsNoWarning()
    {
        Rig rig = new();
        rig.Launcher.Behavior = (_, _) => throw new Win32Exception(UacDeclinedErrorCode);

        ServiceActionOutcome outcome = await rig.Runner.RunAsync(["SaddleRAGMcp"],
                                                                 ServiceAction.Start,
                                                                 TestContext.Current.CancellationToken);

        Assert.Equal(ServiceActionOutcome.Cancelled, outcome);
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task AnotherLaunchErrorIsFailedWithAWarning()
    {
        Rig rig = new();
        rig.Launcher.Behavior = (_, _) => throw new Win32Exception(FileNotFoundErrorCode);

        ServiceActionOutcome outcome = await rig.Runner.RunAsync(["SaddleRAGMcp"],
                                                                 ServiceAction.Start,
                                                                 TestContext.Current.CancellationToken);

        Assert.Equal(ServiceActionOutcome.Failed, outcome);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task SuccessIsLoggedAtInformation()
    {
        Rig rig = new();

        await rig.Runner.RunAsync(["SaddleRAGMcp"], ServiceAction.Start, TestContext.Current.CancellationToken);

        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Information);
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(1)]
    public async Task ADeniedOrFailedActionIsLoggedAtWarningNeverError(int exitCode)
    {
        Rig rig = new();
        rig.Launcher.Behavior = (_, _) => Task.FromResult(exitCode);

        await rig.Runner.RunAsync(["SaddleRAGMcp"], ServiceAction.Start, TestContext.Current.CancellationToken);

        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(rig.Logger.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task AMissingScriptIsScriptMissingAndNothingIsLaunched()
    {
        Rig rig = new();
        rig.Probe.ScriptExists = false;

        ServiceActionOutcome outcome = await rig.Runner.RunAsync(["SaddleRAGMcp"],
                                                                 ServiceAction.Start,
                                                                 TestContext.Current.CancellationToken);

        Assert.Equal(ServiceActionOutcome.ScriptMissing, outcome);
        Assert.Equal(expected: 0, rig.Launcher.Calls);
        Assert.Equal(expected: 0, rig.Guard.Calls);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task AnUnsafeInstallLocationIsRefusedAndNothingIsLaunched()
    {
        Rig rig = new();
        rig.Guard.Verdict = InstallLocationVerdict.Unsafe("E:\\Apps grants BUILTIN\\Users write access");

        ServiceActionOutcome outcome = await rig.Runner.RunAsync(["SaddleRAGMcp"],
                                                                 ServiceAction.Start,
                                                                 TestContext.Current.CancellationToken);

        Assert.Equal(ServiceActionOutcome.UnsafeInstallLocation, outcome);
        Assert.Equal(expected: 0, rig.Launcher.Calls);
        Assert.Equal(smScriptPath, rig.Guard.CheckedPath);
        (LogLevel level, string message) = Assert.Single(rig.Logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("BUILTIN\\Users", message);
        Assert.Contains(smScriptPath, message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("A B")]
    [InlineData("A,B")]
    [InlineData("A;B")]
    [InlineData("A&B")]
    [InlineData("A\"")]
    [InlineData("A\" -Action Stop")]
    [InlineData("A\n")]
    [InlineData("A\r\nB")]
    [InlineData("A$B")]
    [InlineData("A`B")]
    [InlineData("\u00c4")]
    public async Task RejectsAServiceNameThatCouldInjectArguments(string name)
    {
        Rig rig = new();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => rig.Runner.RunAsync(["SaddleRAGMcp", name], ServiceAction.Start, TestContext.Current.CancellationToken));

        Assert.Equal(expected: 0, rig.Launcher.Calls);
    }

    [Fact]
    public async Task RejectsAnEmptyNameList()
    {
        Rig rig = new();

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => rig.Runner.RunAsync([], ServiceAction.Start, TestContext.Current.CancellationToken));

        Assert.Equal(expected: 0, rig.Launcher.Calls);
    }

    [Fact]
    public async Task ValidatesNamesBeforeLookingForTheScript()
    {
        Rig rig = new();
        rig.Probe.ScriptExists = false;

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => rig.Runner.RunAsync(["bad name"], ServiceAction.Start, TestContext.Current.CancellationToken));

        Assert.Equal(expected: 0, rig.Probe.FileExistsCalls);
    }

    [Theory]
    [InlineData("SaddleRAGMcp")]
    [InlineData("MongoDB")]
    [InlineData("My_Service-1.0")]
    public async Task AcceptsOrdinaryServiceNames(string name)
    {
        Rig rig = new();

        ServiceActionOutcome outcome = await rig.Runner.RunAsync([name],
                                                                 ServiceAction.Start,
                                                                 TestContext.Current.CancellationToken);

        Assert.Equal(ServiceActionOutcome.Succeeded, outcome);
    }

    [Fact]
    public void WaitsAtLeastThreeScriptTimeoutsPlusThirtySecondsForTheElevatedProcess()
    {
        TimeSpan minimum = TimeSpan.FromSeconds((3 * 120) + 30);

        Assert.True(ElevatedServiceActionRunner.DefaultProcessWaitTimeout >= minimum);
    }

    [Fact]
    public async Task StopsWaitingAndReportsTimedOutWhenTheElevatedProcessOutlastsTheWait()
    {
        Rig rig = new(TimeSpan.FromMilliseconds(50));
        rig.Launcher.Behavior = async (_, ct) =>
                                {
                                    await Task.Delay(Timeout.Infinite, ct);
                                    return 0;
                                };

        ServiceActionOutcome outcome = await rig.Runner.RunAsync(["SaddleRAGMcp"],
                                                                 ServiceAction.Restart,
                                                                 TestContext.Current.CancellationToken);

        Assert.Equal(ServiceActionOutcome.TimedOut, outcome);
        Assert.Contains(rig.Logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CallerCancellationStopsTheWaitAndPropagates()
    {
        Rig rig = new();
        rig.Launcher.Behavior = async (_, ct) =>
                                {
                                    await Task.Delay(Timeout.Infinite, ct);
                                    return 0;
                                };
        using CancellationTokenSource cts = new();

        Task<ServiceActionOutcome> pending = rig.Runner.RunAsync(["SaddleRAGMcp"], ServiceAction.Stop, cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
