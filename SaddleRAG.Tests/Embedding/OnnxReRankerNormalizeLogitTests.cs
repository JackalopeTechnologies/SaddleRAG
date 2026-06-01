// OnnxReRankerNormalizeLogitTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Locks in the sigmoid contract for cross-encoder logits feeding the
///     blend at <c>SearchTools.BlendRankedResult</c>. Raw logits are
///     unbounded; the blend formula assumes both inputs are in [0, 1]. If
///     anyone removes the sigmoid in <c>ScoreBatch</c>, these tests catch
///     it before another rerank-on top-5 regression ships.
/// </summary>
public sealed class OnnxReRankerNormalizeLogitTests
{
    [Fact]
    public void NormalizeLogitOfZeroReturnsHalf()
    {
        float result = OnnxReRanker.NormalizeLogit(logit: 0f);

        Assert.Equal(expected: 0.5f, result, ScoreTolerance);
    }

    [Fact]
    public void NormalizeLogitOfLargePositiveApproachesOne()
    {
        float result = OnnxReRanker.NormalizeLogit(LargePositiveLogit);

        Assert.True(result > NearOneThreshold,
                    $"Expected sigmoid({LargePositiveLogit}) ~= 1, got {result}"
                   );
        Assert.True(result < 1f, $"Sigmoid must be strictly <1; got {result}");
    }

    [Fact]
    public void NormalizeLogitOfLargeNegativeApproachesZero()
    {
        float result = OnnxReRanker.NormalizeLogit(LargeNegativeLogit);

        Assert.True(result < NearZeroThreshold,
                    $"Expected sigmoid({LargeNegativeLogit}) ~= 0, got {result}"
                   );
        Assert.True(result > 0f, $"Sigmoid must be strictly >0; got {result}");
    }

    [Fact]
    public void NormalizeLogitIsMonotonicallyIncreasing()
    {
        float[] inputs = [-5f, -2f, -1f, 0f, 1f, 2f, 5f];

        float previous = float.NegativeInfinity;
        foreach(float x in inputs)
        {
            float current = OnnxReRanker.NormalizeLogit(x);
            Assert.True(current > previous,
                        $"Sigmoid not monotonic: sigmoid({x})={current} not greater than previous {previous}"
                       );
            previous = current;
        }
    }

    [Fact]
    public void NormalizeLogitClampsAllOutputsIntoOpenZeroOneInterval()
    {
        float[] extremes = [-100f, -50f, -10f, 0f, 10f, 50f, 100f];

        foreach(float x in extremes)
        {
            float result = OnnxReRanker.NormalizeLogit(x);
            Assert.InRange(result, low: 0f, high: 1f);
        }
    }

    private const float ScoreTolerance = 1e-6f;
    private const float LargePositiveLogit = 10f;
    private const float LargeNegativeLogit = -10f;
    private const float NearOneThreshold = 0.999f;
    private const float NearZeroThreshold = 0.001f;
}
