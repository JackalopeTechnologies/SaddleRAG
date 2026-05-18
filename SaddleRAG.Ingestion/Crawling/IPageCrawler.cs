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

internal interface IPageCrawler
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
}
