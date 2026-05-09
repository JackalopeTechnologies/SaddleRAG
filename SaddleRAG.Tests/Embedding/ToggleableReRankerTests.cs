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
        var ranking = new RankingSettings { ReRankerStrategy = ReRankerStrategy.Llm };
        var reranker = BuildReranker(ranking);

        Assert.Equal(ReRankerStrategy.Llm, reranker.Strategy);
    }

    [Theory]
    [InlineData(ReRankerStrategy.Off)]
    [InlineData(ReRankerStrategy.Llm)]
    [InlineData(ReRankerStrategy.CrossEncoder)]
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

        reranker.Strategy = ReRankerStrategy.CrossEncoder;

        Assert.Equal(ReRankerStrategy.CrossEncoder, ranking.ReRankerStrategy);
    }

    private static ToggleableReRanker BuildReranker(RankingSettings ranking) =>
        new ToggleableReRanker(Options.Create(new OllamaSettings()),
                               Options.Create(ranking),
                               NullLoggerFactory.Instance
                              );
}
