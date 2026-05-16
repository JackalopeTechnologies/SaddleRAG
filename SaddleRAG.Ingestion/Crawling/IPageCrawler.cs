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
///     Minimal seam for the streaming-pipeline crawl stage: a single
///     channel-producing async crawl call. The concrete <see cref="PageCrawler" />
///     also offers <c>FetchSinglePageAsync</c> and <c>DryRunAsync</c>; those stay
///     on the concrete type because they are not part of the pipeline contract.
/// </summary>
internal interface IPageCrawler
{
    /// <summary>
    ///     Crawl <paramref name="job" />'s root URL, writing each fetched page
    ///     to <paramref name="output" /> and completing the channel when the
    ///     crawl finishes naturally.
    /// </summary>
    Task CrawlAsync(ScrapeJob job,
                    ChannelWriter<PageRecord> output,
                    string jobId = "",
                    IReadOnlySet<string>? resumeUrls = null,
                    IReadOnlyList<string>? seedUrls = null,
                    Action<int>? onPageFetched = null,
                    Action<int>? onQueued = null,
                    Action? onFetchError = null,
                    CancellationToken ct = default);
}
