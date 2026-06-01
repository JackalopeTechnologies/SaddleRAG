// IngestProgressFormatterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Cli.Handlers;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Tests.Cli;

/// <summary>
///     Pins the saddlerag-cli ingest progress-line format. The line is
///     printed in-place via a leading carriage return on every
///     orchestrator onProgress callback — operators watching ingest jobs
///     rely on the field order and the labels.
/// </summary>
public sealed class IngestProgressFormatterTests
{
    private static ScrapeJobRecord NewProgress(int queued = 5,
                                                int crawled = 10,
                                                int classified = 8,
                                                int chunks = 64,
                                                int searchable = 56,
                                                int pagesCompleted = 8) =>
        new ScrapeJobRecord
            {
                Id = "j1",
                Job = new ScrapeJob
                          {
                              LibraryId = "lib",
                              Version = "v1",
                              RootUrl = "https://example.test/",
                              LibraryHint = "lib",
                              AllowedUrlPatterns = []
                          },
                PagesQueued = queued,
                PagesFetched = crawled,
                PagesClassified = classified,
                ChunksGenerated = chunks,
                ChunksCompleted = searchable,
                PagesCompleted = pagesCompleted
            };

    [Fact]
    public void FormatStartsWithCarriageReturnForInPlaceOverwrite()
    {
        var rendered = IngestProgressFormatter.Format(NewProgress());
        Assert.StartsWith("\r", rendered);
    }

    [Fact]
    public void FormatIncludesAllSixCounterLabelsAndValues()
    {
        var rendered = IngestProgressFormatter
                       .Format(NewProgress(queued: 5, crawled: 10, classified: 8, chunks: 64, searchable: 56, pagesCompleted: 8))
                       .TrimStart('\r');

        Assert.Contains("Queued: 5", rendered);
        Assert.Contains("Crawled: 10", rendered);
        Assert.Contains("Classified: 8", rendered);
        Assert.Contains("Chunks: 64", rendered);
        Assert.Contains("Searchable: 56 chunks", rendered);
        Assert.Contains("(8 pages)", rendered);
    }

    [Fact]
    public void FormatRendersZeroValuesCorrectlyAtStartOfRun()
    {
        var rendered = IngestProgressFormatter
                       .Format(NewProgress(queued: 0, crawled: 0, classified: 0, chunks: 0, searchable: 0, pagesCompleted: 0))
                       .TrimStart('\r');

        Assert.Contains("Queued: 0 | Crawled: 0 | Classified: 0", rendered);
        Assert.Contains("Chunks: 0 | Searchable: 0 chunks (0 pages)", rendered);
    }
}
