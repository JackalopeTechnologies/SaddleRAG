// DeprecationLogTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Reconciliation;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies <see cref="DeprecationLog" /> log-once-per-key semantics
///     under repeated calls and across keys.
/// </summary>
public sealed class DeprecationLogTests
{
    [Fact]
    public void FirstShouldLogReturnsTrue()
    {
        var log = new DeprecationLog();

        bool first = log.ShouldLog(KeyA);

        Assert.True(first);
    }

    [Fact]
    public void SecondShouldLogForSameKeyReturnsFalse()
    {
        var log = new DeprecationLog();

        log.ShouldLog(KeyA);
        bool second = log.ShouldLog(KeyA);

        Assert.False(second);
    }

    [Fact]
    public void DifferentKeysEachLogOnce()
    {
        var log = new DeprecationLog();

        bool firstA = log.ShouldLog(KeyA);
        bool firstB = log.ShouldLog(KeyB);
        bool secondA = log.ShouldLog(KeyA);
        bool secondB = log.ShouldLog(KeyB);

        Assert.True(firstA);
        Assert.True(firstB);
        Assert.False(secondA);
        Assert.False(secondB);
    }

    [Fact]
    public void EmptyKeyThrows()
    {
        var log = new DeprecationLog();

        Assert.Throws<ArgumentException>(() => log.ShouldLog(string.Empty));
    }

    [Fact]
    public void ClearReenablesLogging()
    {
        var log = new DeprecationLog();
        log.ShouldLog(KeyA);

        log.Clear();

        bool afterClear = log.ShouldLog(KeyA);
        Assert.True(afterClear);
    }

    [Fact]
    public void ConcurrentCallsForSameKeyEmitExactlyOneTrue()
    {
        var log = new DeprecationLog();
        const int callerCount = 64;
        int trueCount = 0;

        Parallel.For(fromInclusive: 0,
                     toExclusive: callerCount,
                     i =>
                     {
                         if (log.ShouldLog(KeyA))
                             Interlocked.Increment(ref trueCount);
                     });

        Assert.Equal(1, trueCount);
    }

    private const string KeyA = "scrape_docs.libraryId";

    private const string KeyB = "dryrun_scrape.rootUrl";
}
