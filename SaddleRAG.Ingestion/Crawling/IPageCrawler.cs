// IPageCrawler.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading.Channels;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Crawls a documentation site for the ingestion pipeline.
///     Stream-oriented: each fetched page is written to the supplied
///     <see cref="ChannelWriter{T}" /> as soon as it is parsed so the
///     downstream classify / chunk / embed stages can work in parallel
///     with the crawl. Implementations: <see cref="PageCrawler" /> (live
///     Playwright crawl). Test doubles substitute for this interface to
///     stub crawl output in unit tests.
/// </summary>
public interface IPageCrawler
{
    Task CrawlAsync(ScrapeJob job,
                    ChannelWriter<PageRecord> output,
                    string jobId = "",
                    IReadOnlySet<string>? resumeUrls = null,
                    IReadOnlyList<string>? seedUrls = null,
                    Action<int>? onPageFetched = null,
                    Action<int>? onQueued = null,
                    Action? onFetchError = null,
                    IngestionPersistenceMode persistMode = IngestionPersistenceMode.Full,
                    DryRunAccumulator? dryRunAcc = null,
                    CancellationToken ct = default);

    /// <summary>
    ///     Fetch a single page for an existing (libraryId, version) without
    ///     starting a full crawl. Used by the single-page top-up path on
    ///     <see cref="IngestionOrchestrator.IngestSinglePageAsync" />.
    /// </summary>
    Task<PageRecord?> FetchSinglePageAsync(string libraryId,
                                           string version,
                                           string url,
                                           CancellationToken ct = default);
}
