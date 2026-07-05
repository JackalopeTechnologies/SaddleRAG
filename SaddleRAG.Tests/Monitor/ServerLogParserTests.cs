// ServerLogParserTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class ServerLogParserTests
{
    [Fact]
    public void ParsesSingleLineEntries()
    {
        var entries = ServerLogParser.Parse(
        [
            "2026-07-05 03:23:09.182 -06:00 [INF] \"list_libraries\" completed. IsError = false."
        ]);

        var entry = Assert.Single(entries);
        Assert.Equal(ServerLogLevel.Information, entry.Level);
        Assert.Equal("\"list_libraries\" completed. IsError = false.", entry.Message);
        Assert.Empty(entry.DetailLines);
        Assert.Equal(new DateTimeOffset(2026, 7, 5, 3, 23, 9, 182, TimeSpan.FromHours(-6)),
                     entry.Timestamp
                    );
    }

    [Fact]
    public void GroupsContinuationLinesUnderPrecedingEntry()
    {
        var entries = ServerLogParser.Parse(
        [
            "2026-07-05 03:23:16.400 -06:00 [ERR] \"search_docs\" threw an unhandled exception.",
            "Microsoft.ML.OnnxRuntime.OnnxRuntimeException: [ErrorCode:Fail] device gone",
            "   at SaddleRAG.Something.Embed()",
            "2026-07-05 03:23:31.162 -06:00 [INF] \"get_dashboard_index\" completed. IsError = false."
        ]);

        Assert.Equal(expected: 2, entries.Count);
        Assert.Equal(ServerLogLevel.Error, entries[index: 0].Level);
        Assert.Equal(expected: 2, entries[index: 0].DetailLines.Count);
        Assert.Empty(entries[index: 1].DetailLines);
    }

    [Theory]
    [InlineData("VRB", ServerLogLevel.Verbose)]
    [InlineData("DBG", ServerLogLevel.Debug)]
    [InlineData("INF", ServerLogLevel.Information)]
    [InlineData("WRN", ServerLogLevel.Warning)]
    [InlineData("ERR", ServerLogLevel.Error)]
    [InlineData("FTL", ServerLogLevel.Fatal)]
    public void ParsesAllLevelTokens(string token, ServerLogLevel expected)
    {
        var entries = ServerLogParser.Parse([$"2026-07-05 10:00:00.000 -06:00 [{token}] msg"]);

        Assert.Equal(expected, Assert.Single(entries).Level);
    }

    [Fact]
    public void UnknownLevelTokenLineIsTreatedAsContinuation()
    {
        var entries = ServerLogParser.Parse(
        [
            "2026-07-05 10:00:00.000 -06:00 [INF] first",
            "2026-07-05 10:00:01.000 -06:00 [XXX] not a real level"
        ]);

        var entry = Assert.Single(entries);
        Assert.Single(entry.DetailLines);
    }

    [Fact]
    public void LeadingContinuationLinesWithoutParentAreDropped()
    {
        var entries = ServerLogParser.Parse(
        [
            "tail fragment of a stack trace",
            "   at Something.Else()",
            "2026-07-05 10:00:00.000 -06:00 [WRN] real entry"
        ]);

        var entry = Assert.Single(entries);
        Assert.Equal("real entry", entry.Message);
        Assert.Empty(entry.DetailLines);
    }

    [Fact]
    public void EmptyInputYieldsNoEntries()
    {
        Assert.Empty(ServerLogParser.Parse([]));
    }

    [Fact]
    public void LevelOrderSupportsThresholdComparisons()
    {
        Assert.True(ServerLogLevel.Fatal >= ServerLogLevel.Error);
        Assert.True(ServerLogLevel.Error > ServerLogLevel.Warning);
        Assert.True(ServerLogLevel.Warning > ServerLogLevel.Information);
    }
}
