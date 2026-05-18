// CrawlStage.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     First stage of the streaming ingestion pipeline. Drives
///     <see cref="IPageCrawler.CrawlAsync" /> and forwards page fetched /
///     queued / error callbacks onto the shared <see cref="ScrapeJobRecord" />
///     progress object. Owns the cancel-vs-fatal-error contract: on
///     cancellation it completes the output channel silently; on any other
///     exception it faults the channel with the exception and cancels the
///     shared <see cref="CancellationTokenSource" /> so downstream stages
///     unwind too.
/// </summary>
internal sealed class CrawlStage
{
    public CrawlStage(IPageCrawler crawler, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(crawler);
        ArgumentNullException.ThrowIfNull(logger);
        mCrawler = crawler;
        mLogger = logger;
    }

    private readonly IPageCrawler mCrawler;
    private readonly ILogger mLogger;

    /// <summary>
    ///     Run the crawl stage to completion, cancellation, or fatal error.
    ///     The crawler itself completes <paramref name="output" /> on
    ///     successful end-of-crawl; this method only touches the channel on
    ///     the error/cancel paths.
    /// </summary>
    public async Task RunAsync(ScrapeJob job,
                               ChannelWriter<PageRecord> output,
                               IReadOnlySet<string>? resumeUrls,
                               IReadOnlyList<string>? seedUrls,
                               ScrapeJobRecord progress,
                               Action<ScrapeJobRecord>? onProgress,
                               CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(cts);

        try
        {
            await mCrawler.CrawlAsync(job,
                                      output,
                                      progress.Id,
                                      resumeUrls,
                                      seedUrls,
                                      pageCount =>
                                      {
                                          progress.PagesFetched = pageCount;
                                          onProgress?.Invoke(progress);
                                      },
                                      queueCount => { progress.PagesQueued = queueCount; },
                                      () =>
                                      {
                                          progress.IncrementErrorCount();
                                          onProgress?.Invoke(progress);
                                      },
                                      ct: cts.Token
                                     );
        }
        catch(OperationCanceledException)
        {
            output.TryComplete();
            throw;
        }
        catch(Exception ex)
        {
            mLogger.LogError(ex, "Crawl stage fatal error");
            output.TryComplete(ex);
            await cts.CancelAsync();
            throw;
        }
    }
}
