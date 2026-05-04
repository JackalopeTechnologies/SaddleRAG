// RatesAccumulatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
        var rates = acc.Update(new PipelineCounters { PagesFetched = 10 }, sampleAt: T(0));
        Assert.Equal(0.0, rates.PagesFetchedPerSec, 3);
    }

    [Fact]
    public void RatesComputedFromDeltaAndElapsed()
    {
        var acc = new RatesAccumulator();
        acc.Update(new PipelineCounters { PagesFetched = 10 }, sampleAt: T(0));
        var rates = acc.Update(new PipelineCounters { PagesFetched = 12 }, sampleAt: T(1));
        Assert.Equal(2.0, rates.PagesFetchedPerSec, 3);
    }

    [Fact]
    public void RatesUseTwoMostRecentSamplesOnly()
    {
        var acc = new RatesAccumulator();
        acc.Update(new PipelineCounters { PagesFetched = 0 },   T(0));
        acc.Update(new PipelineCounters { PagesFetched = 100 }, T(10));
        var rates = acc.Update(new PipelineCounters { PagesFetched = 102 }, T(11));
        Assert.Equal(2.0, rates.PagesFetchedPerSec, 3);
    }

    [Fact]
    public void RatesAreZeroWhenSamplesAtSameInstant()
    {
        var acc = new RatesAccumulator();
        acc.Update(new PipelineCounters { PagesFetched = 10 }, T(5));
        var rates = acc.Update(new PipelineCounters { PagesFetched = 12 }, T(5));
        Assert.Equal(0.0, rates.PagesFetchedPerSec, 3);
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
                   T(0));
        var rates = acc.Update(new PipelineCounters
                                   {
                                       PagesFetched = 10,
                                       PagesClassified = 8,
                                       ChunksGenerated = 30,
                                       ChunksEmbedded = 25,
                                       PagesCompleted = 5
                                   },
                               T(2));
        Assert.Equal(5.0,  rates.PagesFetchedPerSec, 3);
        Assert.Equal(4.0,  rates.PagesClassifiedPerSec, 3);
        Assert.Equal(15.0, rates.ChunksGeneratedPerSec, 3);
        Assert.Equal(12.5, rates.ChunksEmbeddedPerSec, 3);
        Assert.Equal(2.5,  rates.PagesCompletedPerSec, 3);
    }

    private static DateTime T(int sec) => new DateTime(year: 2026,
                                                       month: 1,
                                                       day: 1,
                                                       hour: 0,
                                                       minute: 0,
                                                       second: sec,
                                                       DateTimeKind.Utc);
}
