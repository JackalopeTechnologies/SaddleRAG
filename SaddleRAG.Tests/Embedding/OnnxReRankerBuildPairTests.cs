// OnnxReRankerBuildPairTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxReRankerBuildPairTests
{
    [Fact]
    public void BuildPairWithGenerousMaxLenIncludesFullQueryAndDoc()
    {
        var entry = BuildEntry();
        long[] query = [10L, 11L, 12L];
        long[] doc = [20L, 21L, 22L, 23L, 24L];

        long[] pair = OnnxReRanker.BuildPair(entry, query, doc, DefaultGenerousMaxLen);

        // [CLS] q q q [SEP] d d d d d [SEP] => 11 tokens
        Assert.Equal(query.Length + doc.Length + OnnxReRanker.SpecialTokenOverhead, pair.Length);
        Assert.Equal(ClsId, pair[0]);
        Assert.Equal(SepId, pair[query.Length + 1]);
        Assert.Equal(SepId, pair[^1]);
    }

    [Fact]
    public void BuildPairTruncatesDocSideFirstWhenOverLength()
    {
        var entry = BuildEntry();
        long[] query = [10L, 11L];
        long[] doc = [20L, 21L, 22L, 23L, 24L, 25L, 26L, 27L, 28L, 29L];

        // overhead=3, qBudget=min(2, max(0, 10-3-4))=2; dBudget=min(10, max(0, 10-3-2))=5
        long[] pair = OnnxReRanker.BuildPair(entry, query, doc, TruncationMaxLen);

        Assert.Equal(TruncationMaxLen, pair.Length);
        // Query tokens preserved; doc truncated.
        Assert.Equal(query[0], pair[1]);
        Assert.Equal(query[1], pair[2]);
        Assert.Equal(SepId, pair[3]);
    }

    [Fact]
    public void BuildPairAtMinViableSequenceLengthLeavesOneQueryToken()
    {
        var entry = BuildEntry();
        long[] query = [10L, 11L, 12L];
        long[] doc = [20L, 21L, 22L, 23L, 24L];

        // MinViableSequenceLength = overhead(3) + MinDocTokens(4) + 1 = 8.
        // qBudget = min(3, max(0, 8-3-4)) = min(3, 1) = 1; dBudget = min(5, max(0, 8-3-1)) = 4.
        long[] pair = OnnxReRanker.BuildPair(entry, query, doc,
                                             OnnxReRanker.MinViableSequenceLength
                                            );

        Assert.Equal(query[0], pair[1]);
        Assert.Equal(SepId, pair[2]);
    }

    [Fact]
    public void BuildPairBelowMinViableSequenceLengthClampsQueryToZeroTokens()
    {
        // Regression guard. The pathological case OnnxSettingsValidator now
        // rejects at startup: if a misconfigured entry slipped past, this is
        // what would happen at scoring time. The test exists so anyone
        // tempted to lower MinDocTokens or the overhead constant sees the
        // recall-tanking side effect.
        var entry = BuildEntry();
        long[] query = [10L, 11L, 12L];
        long[] doc = [20L, 21L, 22L, 23L, 24L];

        long[] pair = OnnxReRanker.BuildPair(entry, query, doc, BelowMinMaxLen);

        // overhead=3, qBudget=min(3, max(0, 7-3-4))=0, dBudget=min(5, max(0, 7-3-0))=4
        // → [CLS][SEP] d d d d [SEP] : 7 tokens, zero query content.
        Assert.Equal(BelowMinMaxLen, pair.Length);
        Assert.Equal(ClsId, pair[0]);
        Assert.Equal(SepId, pair[1]);
        Assert.Equal(SepId, pair[^1]);
    }

    private static RerankerModelEntry BuildEntry()
    {
        return new RerankerModelEntry
                   {
                       Name = "test-reranker",
                       TokenizerFamily = TokenizerFamily.SentencePiece,
                       SpecialTokens = new Dictionary<string, int>
                                           {
                                               [ClsKey] = (int) ClsId,
                                               [SepKey] = (int) SepId
                                           }
                   };
    }

    private const long ClsId = 1L;
    private const long SepId = 2L;
    private const string ClsKey = "[CLS]";
    private const string SepKey = "[SEP]";
    private const int DefaultGenerousMaxLen = 64;
    private const int TruncationMaxLen = 10;
    private const int BelowMinMaxLen = 7;
}
