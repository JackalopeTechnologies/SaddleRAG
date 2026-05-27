// RatesAccumulatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class RatesAccumulatorTests
{
    [Fact]
    public void RatesReturnsZeroBeforeSecondSample()
    {
        var acc = new RatesAccumulator();
        var rates = acc.Update(new PipelineCounters { PagesFetched = 10 }, T(sec: 0));
        Assert.Equal(expected: 0.0, rates.PagesFetchedPerSec, precision: 3);
    }

    [Fact]
    public void RatesComputedFromDeltaAndElapsed()
    {
        var acc = new RatesAccumulator();
        acc.Update(new PipelineCounters { PagesFetched = 10 }, T(sec: 0));
        var rates = acc.Update(new PipelineCounters { PagesFetched = 12 }, T(sec: 1));
        Assert.Equal(expected: 2.0, rates.PagesFetchedPerSec, precision: 3);
    }

    [Fact]
    public void RatesUseTwoMostRecentSamplesOnly()
    {
        var acc = new RatesAccumulator();
        acc.Update(new PipelineCounters { PagesFetched = 0 }, T(sec: 0));
        acc.Update(new PipelineCounters { PagesFetched = 100 }, T(sec: 10));
        var rates = acc.Update(new PipelineCounters { PagesFetched = 102 }, T(sec: 11));
        Assert.Equal(expected: 2.0, rates.PagesFetchedPerSec, precision: 3);
    }

    [Fact]
    public void RatesAreZeroWhenSamplesAtSameInstant()
    {
        var acc = new RatesAccumulator();
        acc.Update(new PipelineCounters { PagesFetched = 10 }, T(sec: 5));
        var rates = acc.Update(new PipelineCounters { PagesFetched = 12 }, T(sec: 5));
        Assert.Equal(expected: 0.0, rates.PagesFetchedPerSec, precision: 3);
    }

    [Fact]
    public void AllStagesHaveIndependentRates()
    {
        var acc = new RatesAccumulator();
        acc.Update(new PipelineCounters
                       {
                           PagesFetched = 0,
                           PagesClassified = 0,
                           ChunksGenerated = 0,
                           ChunksEmbedded = 0,
                           PagesCompleted = 0
                       },
                   T(sec: 0)
                  );
        var rates = acc.Update(new PipelineCounters
                                   {
                                       PagesFetched = 10,
                                       PagesClassified = 8,
                                       ChunksGenerated = 30,
                                       ChunksEmbedded = 25,
                                       PagesCompleted = 5
                                   },
                               T(sec: 2)
                              );
        Assert.Equal(expected: 5.0, rates.PagesFetchedPerSec, precision: 3);
        Assert.Equal(expected: 4.0, rates.PagesClassifiedPerSec, precision: 3);
        Assert.Equal(expected: 15.0, rates.ChunksGeneratedPerSec, precision: 3);
        Assert.Equal(expected: 12.5, rates.ChunksEmbeddedPerSec, precision: 3);
        Assert.Equal(expected: 2.5, rates.PagesCompletedPerSec, precision: 3);
    }

    private static DateTime T(int sec) => new DateTime(year: 2026,
                                                       month: 1,
                                                       day: 1,
                                                       hour: 0,
                                                       minute: 0,
                                                       sec,
                                                       DateTimeKind.Utc
                                                      );
}
