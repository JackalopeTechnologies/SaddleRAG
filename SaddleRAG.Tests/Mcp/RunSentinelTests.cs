// RunSentinelTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Exercises <see cref="RunSentinel" />, the crash black box (issue #139):
///     a running-marker file written at startup and deleted on graceful stop.
///     A marker already present at startup means the previous run died dirty
///     (native AV, kill, power loss) and its recorded identity is surfaced so
///     the post-mortem knows what was running. Also covers the last-crash
///     marker written by the unhandled-exception hook.
/// </summary>
public sealed class RunSentinelTests : IDisposable
{
    public RunSentinelTests()
    {
        mDirectory = Path.Combine(Path.GetTempPath(), "saddlerag-sentinel-" + Guid.NewGuid().ToString("N"));
    }

    private readonly string mDirectory;

    private const int TestPid = 4242;
    private const string TestVersion = "1.3.1";
    private static readonly DateTime smTestStartUtc = new(2026, 7, 2, 5, 41, 43, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(mDirectory))
            Directory.Delete(mDirectory, recursive: true);
    }

    [Fact]
    public void FirstStartReportsNoPriorRun()
    {
        var sentinel = new RunSentinel(mDirectory);

        RunSentinel.PriorRunInfo? prior = sentinel.MarkStarted(TestPid, TestVersion, smTestStartUtc);

        Assert.Null(prior);
    }

    [Fact]
    public void StartAfterCleanStopReportsNoPriorRun()
    {
        var sentinel = new RunSentinel(mDirectory);
        sentinel.MarkStarted(TestPid, TestVersion, smTestStartUtc);
        sentinel.MarkStoppedCleanly();

        RunSentinel.PriorRunInfo? prior = sentinel.MarkStarted(TestPid, TestVersion, smTestStartUtc);

        Assert.Null(prior);
    }

    [Fact]
    public void StartAfterDirtyStopReportsPriorRunIdentity()
    {
        var firstRun = new RunSentinel(mDirectory);
        firstRun.MarkStarted(TestPid, TestVersion, smTestStartUtc);

        var secondRun = new RunSentinel(mDirectory);
        RunSentinel.PriorRunInfo? prior = secondRun.MarkStarted(9999, "1.3.2", smTestStartUtc.AddHours(1));

        Assert.NotNull(prior);
        Assert.Equal(TestPid, prior.Pid);
        Assert.Equal(TestVersion, prior.Version);
        Assert.Equal(smTestStartUtc, prior.StartedUtc);
    }

    [Fact]
    public void DirtyStopDetectionSurvivesCorruptMarker()
    {
        Directory.CreateDirectory(mDirectory);
        File.WriteAllText(Path.Combine(mDirectory, RunSentinel.RunningMarkerFileName), "not-json{{{");

        var sentinel = new RunSentinel(mDirectory);
        RunSentinel.PriorRunInfo? prior = sentinel.MarkStarted(TestPid, TestVersion, smTestStartUtc);

        Assert.NotNull(prior);
        Assert.Equal(RunSentinel.UnknownPid, prior.Pid);
    }

    [Fact]
    public void WriteCrashMarkerRoundTripsThroughTryReadLastCrash()
    {
        var sentinel = new RunSentinel(mDirectory);
        var boom = new InvalidOperationException("boom");

        sentinel.WriteCrashMarker(boom, smTestStartUtc);
        string? crashText = sentinel.TryReadLastCrash();

        Assert.NotNull(crashText);
        Assert.Contains("boom", crashText, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), crashText, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadLastCrashReturnsNullWhenNoCrashRecorded()
    {
        var sentinel = new RunSentinel(mDirectory);

        Assert.Null(sentinel.TryReadLastCrash());
    }

    [Fact]
    public void MarkStoppedCleanlyWithoutStartDoesNotThrow()
    {
        var sentinel = new RunSentinel(mDirectory);

        sentinel.MarkStoppedCleanly();
    }

    [Fact]
    public void EmptyDirectoryThrows()
    {
        Assert.Throws<ArgumentException>(() => new RunSentinel(string.Empty));
    }
}
