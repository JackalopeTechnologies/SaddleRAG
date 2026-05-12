// ToggleableReRankerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

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

    private static ToggleableReRanker BuildReranker(RankingSettings ranking)
    {
        // Empty Onnx settings → OnnxReRanker resolves to null entry and acts
        // as pass-through. ToggleableReRanker dispatches to it on
        // ReRankerStrategy.Onnx; the test only verifies the Strategy
        // property contract, not actual dispatched calls.
        var onnxOptions = Options.Create(new OnnxSettings());
        var onnxReRanker = new OnnxReRanker(onnxOptions, NullLogger<OnnxReRanker>.Instance);
        var result = new ToggleableReRanker(Options.Create(ranking),
                                            onnxReRanker,
                                            NullLoggerFactory.Instance
                                           );
        return result;
    }
}
