// ToggleableReRankerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class ToggleableReRankerTests
{
    [Fact]
    public void StrategyDefaultsToConfiguredValue()
    {
        var ranking = new RankingSettings { ReRankerStrategy = ReRankerStrategy.Onnx };
        var reranker = BuildReranker(ranking);

        Assert.Equal(ReRankerStrategy.Onnx, reranker.Strategy);
    }

    [Theory]
    [InlineData(ReRankerStrategy.Off)]
    [InlineData(ReRankerStrategy.Onnx)]
    public void StrategySetterMutatesAtRuntime(ReRankerStrategy newStrategy)
    {
        var ranking = new RankingSettings { ReRankerStrategy = ReRankerStrategy.Off };
        var reranker = BuildReranker(ranking);

        reranker.Strategy = newStrategy;

        Assert.Equal(newStrategy, reranker.Strategy);
    }

    [Fact]
    public void StrategySetterFlowsThroughToRankingSettings()
    {
        var ranking = new RankingSettings { ReRankerStrategy = ReRankerStrategy.Off };
        var reranker = BuildReranker(ranking);

        reranker.Strategy = ReRankerStrategy.Onnx;

        Assert.Equal(ReRankerStrategy.Onnx, ranking.ReRankerStrategy);
    }

    [Fact]
    public async Task OnnxWithoutEntryWarningFiresOncePerStrategyTransitionIntoBadState()
    {
        // OnnxReRanker constructed without an active entry → ModelName empty
        // → Strategy=Onnx puts the dispatcher into the contradictory state
        // that ToggleableReRanker.ResolveActive warns about. The fix in
        // commit dea6635 resets the Interlocked dedupe flag in the Strategy
        // setter so a toggle out of and back into the bad state re-emits
        // the warning. Without that reset, only the first transition would
        // log; subsequent ones stay silent for the singleton's lifetime.
        var ranking = new RankingSettings { ReRankerStrategy = ReRankerStrategy.Onnx };
        var captureProvider = new CapturingLoggerProvider();
        var factory = new LoggerFactory(new ILoggerProvider[] { captureProvider });

        var onnxReRanker = new OnnxReRanker(Options.Create(new OnnxSettings()),
                                            new OnnxRuntimeCapabilities(),
                                            NullLogger<OnnxReRanker>.Instance
                                           );
        var reranker = new ToggleableReRanker(Options.Create(ranking), onnxReRanker, factory);

        // First dispatch with Strategy=Onnx + empty model → one warning.
        await reranker.ReRankAsync(QueryText, candidates: [], maxResults: 1,
                                   TestContext.Current.CancellationToken
                                  );
        Assert.Equal(expected: 1, captureProvider.Logger.WarningsContaining(OnnxNoEntryWarningProbe));

        // Repeat dispatch — dedupe holds, no second warning.
        await reranker.ReRankAsync(QueryText, candidates: [], maxResults: 1,
                                   TestContext.Current.CancellationToken
                                  );
        Assert.Equal(expected: 1, captureProvider.Logger.WarningsContaining(OnnxNoEntryWarningProbe));

        // Toggle out of and back into the bad state — dedupe MUST reset.
        reranker.Strategy = ReRankerStrategy.Off;
        reranker.Strategy = ReRankerStrategy.Onnx;
        await reranker.ReRankAsync(QueryText, candidates: [], maxResults: 1,
                                   TestContext.Current.CancellationToken
                                  );
        Assert.Equal(expected: 2, captureProvider.Logger.WarningsContaining(OnnxNoEntryWarningProbe));
    }

    private static ToggleableReRanker BuildReranker(RankingSettings ranking)
    {
        // Empty Onnx settings → OnnxReRanker resolves to null entry and acts
        // as pass-through. ToggleableReRanker dispatches to it on
        // ReRankerStrategy.Onnx; the test only verifies the Strategy
        // property contract, not actual dispatched calls.
        var onnxOptions = Options.Create(new OnnxSettings());
        var capabilities = new OnnxRuntimeCapabilities();
        var onnxReRanker = new OnnxReRanker(onnxOptions, capabilities, NullLogger<OnnxReRanker>.Instance);
        var result = new ToggleableReRanker(Options.Create(ranking),
                                            onnxReRanker,
                                            NullLoggerFactory.Instance
                                           );
        return result;
    }

    private const string QueryText = "probe";
    private const string OnnxNoEntryWarningProbe = "OnnxReRanker has no active entry";

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<string> mWarnings = [];

        IDisposable ILogger.BeginScope<TState>(TState state) where TState : default
        {
            return NullScope.Instance;
        }

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                                 Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                mWarnings.Add(formatter(state, exception));
        }

        public int WarningsContaining(string probe)
        {
            return mWarnings.Count(w => w.Contains(probe, StringComparison.Ordinal));
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public CapturingLogger Logger { get; } = new();

        ILogger ILoggerProvider.CreateLogger(string categoryName) => Logger;

        void IDisposable.Dispose() { }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        void IDisposable.Dispose() { }
    }
}
