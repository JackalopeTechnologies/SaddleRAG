// CrawlPageQuota.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Atomically reserves crawl page slots so parallel workers cannot exceed
///     a configured <c>maxPages</c> limit between their limit check and fetch.
/// </summary>
internal sealed class CrawlPageQuota
{
    internal CrawlPageQuota(int maxPages)
    {
        mMaxPages = maxPages;
    }

    private readonly int mMaxPages;
    private int mReservedOrConsumed;

    internal bool TryReserve()
    {
        var reserved = false;
        if (mMaxPages <= 0)
        {
            reserved = true;
        }
        else
        {
            var current = Volatile.Read(ref mReservedOrConsumed);
            while (!reserved && current < mMaxPages)
            {
                int observed = Interlocked.CompareExchange(ref mReservedOrConsumed, current + 1, current);
                if (observed == current)
                    reserved = true;
                else
                    current = observed;
            }
        }

        return reserved;
    }

    internal void Release()
    {
        if (mMaxPages > 0)
            Interlocked.Decrement(ref mReservedOrConsumed);
    }
}