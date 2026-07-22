// CrawlPageQuotaTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

#region Usings

using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class CrawlPageQuotaTests
{
    [Fact]
    public void TryReserveAllowsAtMostConfiguredLimitAcrossParallelWorkers()
    {
        const int maxPages = 2;
        const int workerCount = 8;
        var quota = new CrawlPageQuota(maxPages);
        var successfulReservations = 0;

        Parallel.For(fromInclusive: 0,
                     toExclusive: workerCount,
                     _ =>
                     {
                         if (quota.TryReserve())
                             Interlocked.Increment(ref successfulReservations);
                     }
                    );

        Assert.Equal(maxPages, successfulReservations);
    }

    [Fact]
    public void ReleaseMakesFailedFetchSlotAvailableAgain()
    {
        var quota = new CrawlPageQuota(maxPages: 1);

        Assert.True(quota.TryReserve());
        Assert.False(quota.TryReserve());

        quota.Release();

        Assert.True(quota.TryReserve());
    }

    [Fact]
    public void UnlimitedQuotaNeverRestrictsReservations()
    {
        var quota = new CrawlPageQuota(maxPages: 0);

        Assert.True(quota.TryReserve());
        Assert.True(quota.TryReserve());
        Assert.True(quota.TryReserve());
    }
}