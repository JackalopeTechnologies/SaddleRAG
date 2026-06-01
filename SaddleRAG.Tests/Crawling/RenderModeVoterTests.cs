// RenderModeVoterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Tests.Crawling;

public sealed class RenderModeVoterTests
{
    [Fact]
    public void IsVoteCompleteIsFalseWithNoSamples()
    {
        var voter = new RenderModeVoter();

        Assert.False(voter.IsVoteComplete);
    }

    [Fact]
    public void RenderModeIsUnknownWithNoSamples()
    {
        var voter = new RenderModeVoter();

        Assert.Equal(RenderMode.Unknown, voter.RenderMode);
    }

    [Fact]
    public void MedianDeltaIsNegativeOneWithNoSamples()
    {
        var voter = new RenderModeVoter();

        Assert.Equal(expected: -1, voter.MedianDelta);
    }

    [Fact]
    public void IsLoadWaitNeededIsTrueBeforeVoteComplete()
    {
        var voter = new RenderModeVoter();

        Assert.True(voter.IsLoadWaitNeeded);
    }

    [Fact]
    public void VoteIsCompleteAfterFiveSamples()
    {
        var voter = new RenderModeVoter();

        for(int i = 0; i < 5; i++)
            voter.RecordSample(domCount: 10, loadCount: 10);

        Assert.True(voter.IsVoteComplete);
    }

    [Fact]
    public void ZeroDeltaSamplesProduceSSRResult()
    {
        var voter = new RenderModeVoter();

        for(int i = 0; i < 5; i++)
            voter.RecordSample(domCount: 20, loadCount: 20);

        Assert.Equal(RenderMode.SSR, voter.RenderMode);
        Assert.False(voter.IsLoadWaitNeeded);
        Assert.Equal(expected: 0, voter.MedianDelta);
    }

    [Fact]
    public void LargeDeltaSamplesProduceSPAResult()
    {
        var voter = new RenderModeVoter();

        for(int i = 0; i < 5; i++)
            voter.RecordSample(domCount: 2, loadCount: 30);

        Assert.Equal(RenderMode.SPA, voter.RenderMode);
        Assert.True(voter.IsLoadWaitNeeded);
        Assert.Equal(expected: 28, voter.MedianDelta);
    }

    [Fact]
    public void MedianIsUsedNotMeanSoOutliersDoNotFlipVote()
    {
        var voter = new RenderModeVoter();

        // 4 SSR pages, 1 SPA outlier — median delta is 0 → SSR
        voter.RecordSample(domCount: 20, loadCount: 20);
        voter.RecordSample(domCount: 15, loadCount: 15);
        voter.RecordSample(domCount: 18, loadCount: 18);
        voter.RecordSample(domCount: 22, loadCount: 22);
        voter.RecordSample(domCount: 5,  loadCount: 50);

        Assert.Equal(RenderMode.SSR, voter.RenderMode);
    }

    [Fact]
    public void SamplesWithNegativeDomCountAreIgnored()
    {
        var voter = new RenderModeVoter();

        voter.RecordSample(domCount: -1, loadCount: 20);
        voter.RecordSample(domCount: 20, loadCount: -1);

        Assert.False(voter.IsVoteComplete);
        Assert.Equal(expected: 0, voter.SamplesRecorded);
    }

    [Fact]
    public void AdditionalSamplesAfterVoteCompleteAreIgnored()
    {
        var voter = new RenderModeVoter();

        for(int i = 0; i < 5; i++)
            voter.RecordSample(domCount: 20, loadCount: 20);

        voter.RecordSample(domCount: 1, loadCount: 100);

        Assert.Equal(expected: 5, voter.SamplesRecorded);
    }

    [Fact]
    public void DeltaAtThresholdBoundaryIsSSR()
    {
        var voter = new RenderModeVoter();

        // Median delta of exactly DeltaThreshold (3) → SSR (≤ threshold)
        for(int i = 0; i < 5; i++)
            voter.RecordSample(domCount: 10, loadCount: 13);

        Assert.Equal(RenderMode.SSR, voter.RenderMode);
    }

    [Fact]
    public void DeltaAboveThresholdIsSPA()
    {
        var voter = new RenderModeVoter();

        // Median delta of 4 → SPA (> threshold)
        for(int i = 0; i < 5; i++)
            voter.RecordSample(domCount: 10, loadCount: 14);

        Assert.Equal(RenderMode.SPA, voter.RenderMode);
    }
}
