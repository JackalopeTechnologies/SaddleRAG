// CrashReportServiceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Mcp;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Exercises <see cref="CrashReportService" /> (issue #140), the
///     aggregation behind the <c>get_crash_report</c> MCP tool: crash dumps,
///     WER report folders, the managed crash marker, event-log records (via
///     the <see cref="ICrashEventReader" /> seam), and the log tail — one
///     call instead of manually correlating four Windows locations.
/// </summary>
public sealed class CrashReportServiceTests : IDisposable
{
    public CrashReportServiceTests()
    {
        mRoot = Path.Combine(Path.GetTempPath(), "saddlerag-crashreport-" + Guid.NewGuid().ToString("N"));
        mDumpFolder = Path.Combine(mRoot, "dumps");
        mWerArchive = Path.Combine(mRoot, "wer", "ReportArchive");
        mWerQueue = Path.Combine(mRoot, "wer", "ReportQueue");
        mLogDirectory = Path.Combine(mRoot, "logs");
        mSentinel = new RunSentinel(Path.Combine(mRoot, "data"));
    }

    private readonly string mRoot;
    private readonly string mDumpFolder;
    private readonly string mWerArchive;
    private readonly string mWerQueue;
    private readonly string mLogDirectory;
    private readonly RunSentinel mSentinel;

    private static readonly DateTime smCrashUtc = new(2026, 7, 2, 12, 33, 54, DateTimeKind.Utc);

    private sealed class FakeEventReader : ICrashEventReader
    {
        public CrashEventInfo? RuntimeCrash { get; init; }
        public CrashEventInfo? Fault { get; init; }
        public CrashEventInfo? ServiceTermination { get; init; }

        public CrashEventInfo? ReadLastRuntimeCrash() => RuntimeCrash;
        public CrashEventInfo? ReadLastFault() => Fault;
        public CrashEventInfo? ReadLastServiceTermination() => ServiceTermination;
    }

    private CrashReportService NewService(ICrashEventReader? reader = null)
    {
        var options = new CrashReportService.Options(mDumpFolder,
                                                     [mWerArchive, mWerQueue],
                                                     mLogDirectory);
        return new CrashReportService(options, mSentinel, reader ?? new FakeEventReader());
    }

    public void Dispose()
    {
        if (Directory.Exists(mRoot))
            Directory.Delete(mRoot, recursive: true);
    }

    [Fact]
    public void EmptyEnvironmentYieldsEmptyReport()
    {
        var report = NewService().Build();

        Assert.Null(report.LastRuntimeCrash);
        Assert.Null(report.LastFault);
        Assert.Null(report.LastServiceTermination);
        Assert.Empty(report.WerReports);
        Assert.Empty(report.CrashDumps);
        Assert.Null(report.LastManagedCrash);
        Assert.Empty(report.LogTail);
    }

    [Fact]
    public void CrashDumpsListedNewestFirstWithSizeAndTimestamp()
    {
        Directory.CreateDirectory(mDumpFolder);
        string older = Path.Combine(mDumpFolder, "SaddleRAG.Mcp.exe.1000.dmp");
        string newer = Path.Combine(mDumpFolder, "SaddleRAG.Mcp.exe.2000.dmp");
        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "newer-and-longer");
        File.SetLastWriteTimeUtc(older, smCrashUtc.AddDays(-1));
        File.SetLastWriteTimeUtc(newer, smCrashUtc);

        var report = NewService().Build();

        Assert.Equal(2, report.CrashDumps.Count);
        Assert.Equal("SaddleRAG.Mcp.exe.2000.dmp", report.CrashDumps[0].FileName);
        Assert.Equal(smCrashUtc, report.CrashDumps[0].LastWriteUtc);
        Assert.Equal("newer-and-longer".Length, report.CrashDumps[0].SizeBytes);
    }

    [Fact]
    public void WerReportsMatchAppCrashPatternAcrossBothRoots()
    {
        Directory.CreateDirectory(Path.Combine(mWerArchive, "AppCrash_SaddleRAG.Mcp.ex_abc"));
        Directory.CreateDirectory(Path.Combine(mWerQueue, "AppCrash_SaddleRAG.Mcp.ex_def"));
        Directory.CreateDirectory(Path.Combine(mWerArchive, "AppCrash_OtherApp.exe_zzz"));

        var report = NewService().Build();

        Assert.Equal(2, report.WerReports.Count);
        Assert.All(report.WerReports, r => Assert.StartsWith("AppCrash_SaddleRAG.Mcp", r.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void ManagedCrashMarkerSurfacesInReport()
    {
        mSentinel.WriteCrashMarker(new InvalidOperationException("kaboom"), smCrashUtc);

        var report = NewService().Build();

        Assert.NotNull(report.LastManagedCrash);
        Assert.Contains("kaboom", report.LastManagedCrash, StringComparison.Ordinal);
    }

    [Fact]
    public void EventReaderResultsFlowThrough()
    {
        var reader = new FakeEventReader
            {
                RuntimeCrash = new CrashEventInfo(smCrashUtc, "OgaCreateGenerator stack"),
                Fault = new CrashEventInfo(smCrashUtc, "0xc0000005 in coreclr.dll"),
                ServiceTermination = new CrashEventInfo(smCrashUtc, "terminated unexpectedly")
            };

        var report = NewService(reader).Build();

        Assert.Equal("OgaCreateGenerator stack", report.LastRuntimeCrash?.Message);
        Assert.Equal("0xc0000005 in coreclr.dll", report.LastFault?.Message);
        Assert.Equal("terminated unexpectedly", report.LastServiceTermination?.Message);
    }

    [Fact]
    public void LogTailReturnsLastLinesOfLatestLogFile()
    {
        Directory.CreateDirectory(mLogDirectory);
        var lines = Enumerable.Range(1, 30).Select(i => $"line-{i}").ToArray();
        File.WriteAllLines(Path.Combine(mLogDirectory, "saddlerag-20260702.log"), lines);

        var report = NewService().Build();

        Assert.Equal(CrashReportService.LogTailLineCount, report.LogTail.Count);
        Assert.Equal("line-30", report.LogTail[^1]);
        Assert.Equal($"line-{30 - CrashReportService.LogTailLineCount + 1}", report.LogTail[0]);
    }
}
