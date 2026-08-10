// LogBadgeAcknowledgementTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Globalization;
using System.Reflection;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Mcp;
using SaddleRAG.Mcp.Monitor;
using SaddleRAG.Monitor.Pages;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     The Logs nav badge is a call to action, so reading the Logs page has to answer it.
///     Exercised against the real log reader over a temp directory: a badge that clears
///     only in a stubbed world would still nag against real files.
/// </summary>
public sealed class LogBadgeAcknowledgementTests : IDisposable
{
    public LogBadgeAcknowledgementTests()
    {
        mDirectory = Path.Combine(Path.GetTempPath(), $"saddlerag-badgetests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(mDirectory))
            Directory.Delete(mDirectory, recursive: true);
    }

    [Fact]
    public void AcknowledgementStartsUnsetAndOnlyEverMovesForward()
    {
        var acknowledgement = new ServerLogAcknowledgement();
        DateTimeOffset viewed = DateTimeOffset.UtcNow;

        Assert.Null(acknowledgement.AcknowledgedThrough);

        acknowledgement.AcknowledgeThrough(viewed);
        Assert.Equal(viewed, acknowledgement.AcknowledgedThrough);

        acknowledgement.AcknowledgeThrough(viewed.AddMinutes(minutes: -5));
        Assert.Equal(viewed, acknowledgement.AcknowledgedThrough);

        acknowledgement.AcknowledgeThrough(viewed.AddMinutes(minutes: 5));
        Assert.Equal(viewed.AddMinutes(minutes: 5), acknowledgement.AcknowledgedThrough);
    }

    [Fact]
    public async Task BadgeCountsRecentErrorsUntilTheLogsPageIsViewed()
    {
        WriteLog(Line(MinutesAgo(minutes: 30), "ERR", "first failure"),
                 Line(MinutesAgo(minutes: 10), "FTL", "second failure"));
        var reader = new FileServerLogTailReader(mDirectory);
        var acknowledgement = new ServerLogAcknowledgement();

        Assert.Equal(expected: 2, await CountBadgeAsync(reader, acknowledgement));

        await ViewLogsPageAsync(reader, acknowledgement);

        Assert.Equal(expected: 0, await CountBadgeAsync(reader, acknowledgement));
    }

    [Fact]
    public async Task BadgeCountsOnlyFailuresNewerThanTheLastViewing()
    {
        WriteLog(Line(MinutesAgo(minutes: 40), "ERR", "seen already"),
                 Line(MinutesAgo(minutes: 5), "ERR", "arrived after the last viewing"));
        var reader = new FileServerLogTailReader(mDirectory);
        var acknowledgement = new ServerLogAcknowledgement();
        acknowledgement.AcknowledgeThrough(DateTimeOffset.UtcNow.AddMinutes(minutes: -20));

        Assert.Equal(expected: 1, await CountBadgeAsync(reader, acknowledgement));
    }

    [Fact]
    public async Task ViewingTheLogsPageAcknowledgesThroughTheNewestEntryOnScreen()
    {
        DateTimeOffset newest = MinutesAgo(minutes: 3);
        WriteLog(Line(MinutesAgo(minutes: 20), "INF", "older"),
                 Line(newest, "ERR", "newest"));
        var acknowledgement = new ServerLogAcknowledgement();

        await ViewLogsPageAsync(new FileServerLogTailReader(mDirectory), acknowledgement);

        Assert.NotNull(acknowledgement.AcknowledgedThrough);
        Assert.Equal(newest.ToUnixTimeSeconds(), acknowledgement.AcknowledgedThrough!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task AFailedLogReadAcknowledgesNothing()
    {
        var acknowledgement = new ServerLogAcknowledgement();

        await ViewLogsPageAsync(new UnreadableLogReader(), acknowledgement);

        Assert.Null(acknowledgement.AcknowledgedThrough);
    }

    private static async Task<int> CountBadgeAsync(IServerLogReader reader,
                                                    IServerLogAcknowledgement acknowledgement)
    {
        using var layout = new TestableMainLayout(reader, acknowledgement);
        await layout.InitializeAsync();
        return layout.RecentErrorCountForTest;
    }

    private static async Task ViewLogsPageAsync(IServerLogReader reader,
                                                 IServerLogAcknowledgement acknowledgement)
    {
        using var page = new TestableLogsPage(reader, acknowledgement);
        await page.InitializeAsync();
    }

    private sealed class TestableMainLayout : MainLayoutBase
    {
        public TestableMainLayout(IServerLogReader reader, IServerLogAcknowledgement acknowledgement)
        {
            InjectionHelper.Set<MainLayoutBase>(this, "LogReader", reader);
            InjectionHelper.Set<MainLayoutBase>(this, "Acknowledgement", acknowledgement);
        }

        public int RecentErrorCountForTest => RecentErrorCount;
        public Task InitializeAsync() => OnInitializedAsync();
    }

    private sealed class TestableLogsPage : LogsPageBase
    {
        public TestableLogsPage(IServerLogReader reader, IServerLogAcknowledgement acknowledgement)
        {
            InjectionHelper.Set<LogsPageBase>(this, "LogReader", reader);
            InjectionHelper.Set<LogsPageBase>(this, "Acknowledgement", acknowledgement);
        }

        public Task InitializeAsync() => OnInitializedAsync();
    }

    private static class InjectionHelper
    {
        public static void Set<TComponent>(TComponent component, string propertyName, object value)
        {
            PropertyInfo? property = typeof(TComponent).GetProperty(propertyName,
                                                                     BindingFlags.Instance
                                                                     | BindingFlags.NonPublic);
            Assert.NotNull(property);
            property.SetValue(component, value);
        }
    }

    private sealed class UnreadableLogReader : IServerLogReader
    {
        public ServerLogSnapshot Read(int maxEntries) => throw new IOException("log file is locked");

        public int CountRecentErrors(TimeSpan window) => throw new IOException("log file is locked");
    }

    private void WriteLog(params string[] lines)
    {
        File.WriteAllLines(Path.Combine(mDirectory, "saddlerag-20260810.log"), lines);
    }

    private static string Line(DateTimeOffset timestamp, string level, string message)
    {
        string stamp = timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        return $"{stamp} [{level}] {message}";
    }

    private static DateTimeOffset MinutesAgo(int minutes) => DateTimeOffset.Now.AddMinutes(-minutes);

    private readonly string mDirectory;
}
