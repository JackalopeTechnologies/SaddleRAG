// CrawlOutcomeEvaluator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion;

/// <summary>
///     Pure terminal-outcome heuristic for "this crawl did not actually harvest the site."
///     No I/O — the caller passes the finished job's counters.
///     <para>
///         A scrape that throws is already reported as failed. The gap this closes is the
///         scrape that throws nothing and harvests nothing: the advanced-installer job
///         indexed 1 page, logged roughly 1,485 page errors, and reported
///         <c>Completed</c>, so every downstream consumer treated the library as usable
///         and answered queries from a single page of footer boilerplate for two weeks.
///         An index that looks like a hit suppresses the fallback to web search, which
///         makes it worse than no index at all.
///     </para>
///     <para>
///         The test is deliberately evidence-based rather than size-based: a small
///         documentation set is legitimate, but errors vastly outnumbering indexed pages
///         is positive proof the crawl broke. A genuinely tiny library that crawls cleanly
///         still completes, so the gate cannot fire on a healthy scrape.
///     </para>
/// </summary>
public static class CrawlOutcomeEvaluator
{
    /// <summary>
    ///     True when the finished crawl must not claim success: it either indexed nothing
    ///     at all, or recorded page errors that dwarf what it managed to index.
    ///     <paramref name="errorCount" /> must clear
    ///     <see cref="MinimumSignificantErrors" /> as well as the ratio, so a handful of
    ///     dead links on a small site is not mistaken for a broken crawl.
    /// </summary>
    public static bool IndicatesFailedCrawl(int pagesCompleted, int errorCount)
    {
        bool indexedNothing = pagesCompleted <= 0;
        bool errorsDominate = errorCount >= MinimumSignificantErrors &&
                              errorCount > pagesCompleted * ErrorDominanceFactor;

        return indexedNothing || errorsDominate;
    }

    private const int MinimumSignificantErrors = 10;
    private const int ErrorDominanceFactor = 10;
}
