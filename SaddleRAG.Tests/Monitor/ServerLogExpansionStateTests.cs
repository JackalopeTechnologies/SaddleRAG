// ServerLogExpansionStateTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class ServerLogExpansionStateTests
{
    private static ServerLogEntry Entry(string message = "boom", int second = 30) =>
        new()
        {
            Timestamp = new DateTimeOffset(2026, 8, 10, 7, 33, second, TimeSpan.Zero),
            Level = ServerLogLevel.Error,
            Message = message,
            // A fresh list instance every call, exactly like a re-read snapshot produces.
            DetailLines = ["at Foo.Bar()", "at Baz.Qux()"]
        };

    [Fact]
    public void NothingIsExpandedInitially()
    {
        ServerLogExpansionState state = new();

        Assert.False(state.IsExpanded(Entry()));
        Assert.Equal(expected: 0, state.ExpandedCount);
    }

    [Fact]
    public void TogglingExpandsTheEntry()
    {
        ServerLogExpansionState state = new();
        ServerLogEntry entry = Entry();

        state.Toggle(entry);

        Assert.True(state.IsExpanded(entry));
        Assert.Equal(expected: 1, state.ExpandedCount);
    }

    [Fact]
    public void TogglingTwiceCollapsesTheEntry()
    {
        ServerLogExpansionState state = new();
        ServerLogEntry entry = Entry();

        state.Toggle(entry);
        state.Toggle(entry);

        Assert.False(state.IsExpanded(entry));
        Assert.Equal(expected: 0, state.ExpandedCount);
    }

    [Fact]
    public void AnExpandedRowSurvivesTheNextSnapshotOfTheSameLogLine()
    {
        // The regression: the log page re-reads every two seconds and gets entirely new
        // ServerLogEntry instances. Tracking expansion by object identity meant the row the
        // user had opened silently closed on the next tick.
        ServerLogExpansionState state = new();
        state.Toggle(Entry());

        ServerLogEntry sameLineFromNextRead = Entry();

        Assert.NotSame(Entry(), sameLineFromNextRead);
        Assert.True(state.IsExpanded(sameLineFromNextRead));
    }

    [Fact]
    public void ADifferentLogLineIsNotExpandedByAssociation()
    {
        ServerLogExpansionState state = new();
        state.Toggle(Entry("boom"));

        Assert.False(state.IsExpanded(Entry("something else")));
    }

    [Fact]
    public void EntriesAtDifferentTimesAreTrackedSeparately()
    {
        ServerLogExpansionState state = new();
        state.Toggle(Entry(second: 30));

        Assert.False(state.IsExpanded(Entry(second: 31)));
    }

    [Fact]
    public void CollapsingEverythingClearsTheCount()
    {
        ServerLogExpansionState state = new();
        state.Toggle(Entry("one"));
        state.Toggle(Entry("two"));

        state.CollapseAll();

        Assert.Equal(expected: 0, state.ExpandedCount);
        Assert.False(state.IsExpanded(Entry("one")));
    }
}
