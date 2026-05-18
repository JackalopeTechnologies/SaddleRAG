// ChunkStageTimingTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Chunking;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies that ChunkStage emits an Information-level log entry per
///     page processed with the URL, elapsed milliseconds, and chunk count.
/// </summary>
public sealed class ChunkStageTimingTests
{
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class FixedChunker : IChunker
    {
        public IReadOnlyList<DocChunk> Chunk(PageRecord page, LibraryProfile? libraryProfile = null) =>
            new[]
                {
                    new DocChunk
                        {
                            Id = page.Url + "#0",
                            LibraryId = page.LibraryId,
                            Version = page.Version,
                            PageUrl = page.Url,
                            PageTitle = page.Title,
                            Category = DocCategory.HowTo,
                            Content = "c0"
                        }
                };
    }

    private static PageRecord NewPage() => new()
                                               {
                                                   Id = "p1",
                                                   LibraryId = "lib",
                                                   Version = "v1",
                                                   Url = "https://example.test/p1",
                                                   Title = "t",
                                                   Category = DocCategory.HowTo,
                                                   RawContent = "c",
                                                   FetchedAt = DateTime.UtcNow,
                                                   ContentHash = "h"
                                               };

    private static ScrapeJobRecord NewProgress() => new()
                                                        {
                                                            Id = "job-1",
                                                            Job = new ScrapeJob
                                                                      {
                                                                          LibraryId = "lib",
                                                                          Version = "v1",
                                                                          RootUrl = "https://example.test/",
                                                                          LibraryHint = "lib",
                                                                          AllowedUrlPatterns = []
                                                                      }
                                                        };

    [Fact]
    public async Task RunAsyncLogsElapsedMillisecondsAndChunkCountAtInformationLevel()
    {
        var logger = new CapturingLogger();
        var stage = new ChunkStage(new FixedChunker(), Substitute.For<IMonitorBroadcaster>(), logger);

        var input = Channel.CreateBounded<PageRecord>(2);
        var output = Channel.CreateBounded<DocChunk[]>(2);
        await input.Writer.WriteAsync(NewPage(), TestContext.Current.CancellationToken);
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        await stage.RunAsync(input.Reader, output.Writer, NewProgress(), null, cts);

        var info = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Information && e.Message.Contains("Chunked"));
        Assert.NotEqual(default, info);
        Assert.Contains("https://example.test/p1", info.Message);
        Assert.Contains("count=1", info.Message);
        Assert.Contains("ms", info.Message);
    }
}
