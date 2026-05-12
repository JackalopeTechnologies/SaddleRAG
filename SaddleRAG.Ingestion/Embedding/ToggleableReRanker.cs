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
///     Strategy-aware reranker dispatcher. Holds the active rerankers
///     (NoOp pass-through and the ONNX cross-encoder) and routes each
///     ReRankAsync call per <see cref="RankingSettings.ReRankerStrategy" />.
///     The <see cref="Strategy" /> property is a runtime-mutable view over
///     the same setting; writing to it is visible to every other consumer
///     of RankingSettings. Use the <c>set_rerank_strategy</c> MCP tool to
///     flip between Off and Onnx without a restart.
/// </summary>
public class ToggleableReRanker : IReRanker
{
    public ToggleableReRanker(IOptions<RankingSettings> rankingSettings,
                              OnnxReRanker onnxReRanker,
                              ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(rankingSettings);
        ArgumentNullException.ThrowIfNull(onnxReRanker);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        mRankingSettings = rankingSettings.Value;
        mOnnxReRanker = onnxReRanker;
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

    private readonly ILogger<ToggleableReRanker> mLogger;
    private readonly NoOpReRanker mNoOpReRanker;
    private readonly OnnxReRanker mOnnxReRanker;
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
        var strategy = Strategy;
        IReRanker result = strategy switch
            {
                ReRankerStrategy.Off => mNoOpReRanker,
                ReRankerStrategy.Onnx => mOnnxReRanker,
                var _ => mNoOpReRanker
            };
        return result;
    }
}
