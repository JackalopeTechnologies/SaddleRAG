// ToggleableReRanker.cs
// Copyright (c) 2012-Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Strategy-aware reranker dispatcher. Holds three concrete rerankers
///     (NoOp, Ollama LLM, CrossEncoder) and dispatches per call to the
///     strategy named in RankingSettings.ReRankerStrategy. The Strategy
///     property is a runtime-mutable view over the same setting; writing
///     to it is visible to every consumer that reads RankingSettings
///     (e.g. SearchTools' diagnostic Strategy field). Use the
///     set_rerank_strategy MCP tool to flip strategies without a restart.
/// </summary>
public class ToggleableReRanker : IReRanker
{
    public ToggleableReRanker(IOptions<OllamaSettings> ollamaSettings,
                              IOptions<RankingSettings> rankingSettings,
                              ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(ollamaSettings);
        ArgumentNullException.ThrowIfNull(rankingSettings);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        mRankingSettings = rankingSettings.Value;
        mOllamaReRanker = new OllamaReRanker(ollamaSettings, loggerFactory.CreateLogger<OllamaReRanker>());
        mCrossEncoderReRanker =
            new CrossEncoderReRanker(ollamaSettings, loggerFactory.CreateLogger<CrossEncoderReRanker>());
        mNoOpReRanker = new NoOpReRanker();
        mLogger = loggerFactory.CreateLogger<ToggleableReRanker>();
    }

    /// <summary>
    ///     The active reranker strategy. Backed by
    ///     RankingSettings.ReRankerStrategy so reads and writes are
    ///     consistent with every other consumer of that setting.
    /// </summary>
    public ReRankerStrategy Strategy
    {
        get => mRankingSettings.ReRankerStrategy;
        set
        {
            mRankingSettings.ReRankerStrategy = value;
            mLogger.LogInformation("Re-ranking strategy set to {Strategy}", value);
        }
    }

    private readonly CrossEncoderReRanker mCrossEncoderReRanker;
    private readonly ILogger<ToggleableReRanker> mLogger;
    private readonly NoOpReRanker mNoOpReRanker;

    private readonly OllamaReRanker mOllamaReRanker;
    private readonly RankingSettings mRankingSettings;

    /// <inheritdoc />
    public Task<IReadOnlyList<ReRankResult>> ReRankAsync(string query,
                                                         IReadOnlyList<DocChunk> candidates,
                                                         int maxResults,
                                                         CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);

        var active = ResolveActive();
        var result = active.ReRankAsync(query, candidates, maxResults, ct);
        return result;
    }

    private IReRanker ResolveActive()
    {
        // Llm dispatch is re-enabled with the rewritten OllamaReRanker
        // (per-pair continuous-float scoring against phi4-mini, replacing
        // the legacy 5-bucket categorical plateau). CrossEncoder stays
        // routed to NoOp because the rjmalagon/mxbai-rerank-large-v2
        // Ollama port lost its generate capability upstream — re-enable
        // when a real cross-encoder runtime is wired up (HuggingFace TEI
        // sidecar with /api/rerank).
        var strategy = Strategy;
        var result = strategy switch
            {
                ReRankerStrategy.Off => (IReRanker) mNoOpReRanker,
                ReRankerStrategy.Llm => mOllamaReRanker,
                ReRankerStrategy.CrossEncoder => mNoOpReRanker,
                var _ => mNoOpReRanker
            };
        // mCrossEncoderReRanker is kept instantiated for future
        // re-enable; discard read silences the unused-field warning
        // without #pragma suppression.
        _ = mCrossEncoderReRanker;
        return result;
    }
}
