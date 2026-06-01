// EmbedStageTimingTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies that EmbedStage emits an Information-level log entry per
///     batch including chunk count and elapsed milliseconds.
/// </summary>
public sealed class EmbedStageTimingTests
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

    private static DocChunk NewChunk(string id) => new()
                                                       {
                                                           Id = id,
                                                           LibraryId = "lib",
                                                           Version = "v1",
                                                           PageUrl = $"https://example.test/{id}",
                                                           PageTitle = "t",
                                                           Category = DocCategory.HowTo,
                                                           Content = $"c-{id}"
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
    public async Task RunAsyncLogsBatchSizeAndElapsedMillisecondsAtInformationLevel()
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                         {
                             var texts = call.Arg<IReadOnlyList<string>>();
                             var result = new float[texts.Count][];
                             for(var i = 0; i < texts.Count; i++)
                                 result[i] = new float[4];
                             return Task.FromResult(result);
                         }
                        );

        var logger = new CapturingLogger();
        var stage = new EmbedStage(provider,
                                   Substitute.For<IChunkRepository>(),
                                   Substitute.For<IMonitorBroadcaster>(),
                                   logger);

        var input = Channel.CreateBounded<DocChunk[]>(4);
        var output = Channel.CreateBounded<DocChunk[]>(4);
        await input.Writer.WriteAsync(new[] { NewChunk("a"), NewChunk("b") },
                                      TestContext.Current.CancellationToken
                                     );
        input.Writer.Complete();

        using var cts = new CancellationTokenSource();
        await stage.RunAsync(input.Reader, output.Writer, NewProgress(), null, cts);

        var info = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Information
                                                      && e.Message.Contains("Embedded"));
        Assert.NotEqual(default, info);
        Assert.Contains("count=2", info.Message);
        Assert.Contains("ms", info.Message);
    }
}
