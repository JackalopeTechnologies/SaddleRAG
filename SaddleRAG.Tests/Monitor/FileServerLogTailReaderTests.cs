// FileServerLogTailReaderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Globalization;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class FileServerLogTailReaderTests : IDisposable
{
    public FileServerLogTailReaderTests()
    {
        mDirectory = Path.Combine(Path.GetTempPath(), $"saddlerag-logtests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(mDirectory))
            Directory.Delete(mDirectory, recursive: true);
    }

    [Fact]
    public void MissingDirectoryYieldsEmptySnapshot()
    {
        var reader = new FileServerLogTailReader(Path.Combine(mDirectory, "nope"));

        var snapshot = reader.Read(maxEntries: 100);

        Assert.Null(snapshot.LogFileName);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void ReadsNewestFileByLastWriteTime()
    {
        WriteLog("saddlerag-20260701.log", Line(DaysAgo(days: 4), "INF", "old"));
        WriteLog("saddlerag-20260705.log", Line(DaysAgo(days: 0), "INF", "new"));
        File.SetLastWriteTimeUtc(Path.Combine(mDirectory, "saddlerag-20260701.log"),
                                 DateTime.UtcNow.AddDays(-4)
                                );

        var snapshot = new FileServerLogTailReader(mDirectory).Read(maxEntries: 10);

        Assert.Equal("saddlerag-20260705.log", snapshot.LogFileName);
        Assert.Equal("new", Assert.Single(snapshot.Entries).Message);
    }

    [Fact]
    public void EntriesAreNewestFirstAndCapped()
    {
        WriteLog("saddlerag-20260705.log",
                 Line(MinutesAgo(minutes: 3), "INF", "one"),
                 Line(MinutesAgo(minutes: 2), "INF", "two"),
                 Line(MinutesAgo(minutes: 1), "INF", "three")
                );

        var snapshot = new FileServerLogTailReader(mDirectory).Read(maxEntries: 2);

        Assert.Equal(expected: 2, snapshot.Entries.Count);
        Assert.Equal("three", snapshot.Entries[index: 0].Message);
        Assert.Equal("two", snapshot.Entries[index: 1].Message);
        Assert.Equal(expected: 3, snapshot.TotalEntriesInWindow);
    }

    [Fact]
    public void ReadsWhileWriterHoldsTheFileOpen()
    {
        string path = Path.Combine(mDirectory, "saddlerag-20260705.log");
        using var writer = new StreamWriter(new FileStream(path,
                                                           FileMode.Create,
                                                           FileAccess.Write,
                                                           FileShare.ReadWrite
                                                          ));
        writer.WriteLine(Line(MinutesAgo(minutes: 1), "ERR", "held open"));
        writer.Flush();

        var snapshot = new FileServerLogTailReader(mDirectory).Read(maxEntries: 10);

        Assert.Equal("held open", Assert.Single(snapshot.Entries).Message);
    }

    [Fact]
    public void LargeFileIsClippedToTailWindowAndFlagged()
    {
        string filler = new string(c: 'x', count: 200);
        var lines = new List<string>();
        for(var i = 0; i < LineCountForOverflow; i++)
            lines.Add(Line(MinutesAgo(minutes: 1), "INF", $"{filler} {i}"));
        WriteLog("saddlerag-20260705.log", lines.ToArray());

        var snapshot = new FileServerLogTailReader(mDirectory).Read(maxEntries: int.MaxValue);

        Assert.True(snapshot.TruncatedAtWindow);
        Assert.True(snapshot.TotalEntriesInWindow < LineCountForOverflow);
        Assert.True(snapshot.TotalEntriesInWindow > 0);
    }

    [Fact]
    public void CountRecentErrorsHonorsWindowAndLevel()
    {
        WriteLog("saddlerag-20260705.log",
                 Line(MinutesAgo(minutes: 90), "ERR", "too old"),
                 Line(MinutesAgo(minutes: 30), "ERR", "counted"),
                 Line(MinutesAgo(minutes: 20), "FTL", "counted"),
                 Line(MinutesAgo(minutes: 10), "WRN", "not an error")
                );

        int count = new FileServerLogTailReader(mDirectory).CountRecentErrors(TimeSpan.FromHours(hours: 1));

        Assert.Equal(expected: 2, count);
    }

    private void WriteLog(string fileName, params string[] lines)
    {
        File.WriteAllLines(Path.Combine(mDirectory, fileName), lines);
    }

    private static string Line(DateTimeOffset timestamp, string level, string message)
    {
        string stamp = timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        return $"{stamp} [{level}] {message}";
    }

    private static DateTimeOffset MinutesAgo(int minutes) => DateTimeOffset.Now.AddMinutes(-minutes);

    private static DateTimeOffset DaysAgo(int days) => DateTimeOffset.Now.AddDays(-days);

    private readonly string mDirectory;

    private const int LineCountForOverflow = 4000;
}
