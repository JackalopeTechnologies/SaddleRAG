// ClassifyStageTimingTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Classification;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies that ClassifyStage emits an Information-level log entry per
///     classified page containing the URL, elapsed milliseconds, resolved
///     category, and confidence. Captures log entries via a lightweight
///     test logger because NullLogger discards them.
/// </summary>
public sealed class ClassifyStageTimingTests
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

    private sealed class FixedClassifier : ILlmClassifier
    {
        public string BackendName => "stub";
        public string ModelId => string.Empty;

        public Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                            string libraryHint,
                                                                            CancellationToken ct = default) =>
            Task.FromResult((DocCategory.HowTo, 0.91f));

        public string GetCurrentVersion() => $"{ModelId}-v1";
    }

    private static PageRecord NewPage() => new()
                                               {
                                                   Id = "p1",
                                                   LibraryId = "lib",
                                                   Version = "v1",
                                                   Url = "https://example.test/p1",
                                                   Title = "t",
                                                   Category = DocCategory.Unclassified,
                                                   RawContent = "c",
                                                   FetchedAt = DateTime.UtcNow,
                                                   ContentHash = "h"
                                               };

    [Fact]
    public async Task ClassifyPageAsyncLogsElapsedMillisecondsAndCategoryAtInformationLevel()
    {
        var logger = new CapturingLogger();
        var stage = new ClassifyStage(new FixedClassifier(),
                                      Substitute.For<IPageRepository>(),
                                      Substitute.For<IMonitorBroadcaster>(),
                                      logger
                                     );

        await stage.ClassifyPageAsync(NewPage(), "lib-hint");

        var info = logger.Entries.FirstOrDefault(e => e.Level == LogLevel.Information);
        Assert.NotEqual(default, info);
        Assert.Contains("Classified", info.Message);
        Assert.Contains("https://example.test/p1", info.Message);
        Assert.Contains("HowTo", info.Message);
        Assert.Contains("ms", info.Message);
    }
}
