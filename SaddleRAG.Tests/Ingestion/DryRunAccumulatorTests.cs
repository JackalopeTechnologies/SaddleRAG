// DryRunAccumulatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Runtime.CompilerServices;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies the in-memory accumulator the dry-run pipeline populates
///     in place of repository writes. Counts and timing aggregates are
///     thread-safe under concurrent recorders.
/// </summary>
public sealed class DryRunAccumulatorTests
{
    [Fact]
    public void RecordFetchMsAggregatesTotalAndCount()
    {
        var acc = new DryRunAccumulator();
        acc.RecordFetchMs(10);
        acc.RecordFetchMs(25);

        var snap = acc.Snapshot();
        Assert.Equal(2, snap.Timings.FetchSampleCount);
        Assert.Equal(35, snap.Timings.TotalFetchMs);
    }

    [Fact]
    public void RecordClassifiedBumpsCategoryHistogram()
    {
        var acc = new DryRunAccumulator();
        acc.RecordClassified(DocCategory.HowTo, classifyMs: 5);
        acc.RecordClassified(DocCategory.HowTo, classifyMs: 7);
        acc.RecordClassified(DocCategory.Sample, classifyMs: 3);

        var snap = acc.Snapshot();
        Assert.Equal(2, snap.CategoryHistogram[DocCategory.HowTo]);
        Assert.Equal(1, snap.CategoryHistogram[DocCategory.Sample]);
        Assert.Equal(15, snap.Timings.TotalClassifyMs);
        Assert.Equal(3, snap.Timings.ClassifySampleCount);
    }

    [Fact]
    public void RecordSamplePageRespectsLimit()
    {
        var acc = new DryRunAccumulator(samplePageLimit: 2);
        acc.RecordSamplePage(new DryRunPageEntry
                                 {
                                     Url = "https://a/",
                                     OutOfScopeDepth = 0,
                                     InScope = true,
                                     ContentBytes = 100,
                                     LinksFound = 1,
                                     ContentNodesAtDom = -1,
                                     ContentNodesAtLoad = -1
                                 });
        acc.RecordSamplePage(new DryRunPageEntry
                                 {
                                     Url = "https://b/",
                                     OutOfScopeDepth = 0,
                                     InScope = true,
                                     ContentBytes = 100,
                                     LinksFound = 1,
                                     ContentNodesAtDom = -1,
                                     ContentNodesAtLoad = -1
                                 });
        acc.RecordSamplePage(new DryRunPageEntry
                                 {
                                     Url = "https://c/",
                                     OutOfScopeDepth = 0,
                                     InScope = true,
                                     ContentBytes = 100,
                                     LinksFound = 1,
                                     ContentNodesAtDom = -1,
                                     ContentNodesAtLoad = -1
                                 });

        var snap = acc.Snapshot();
        Assert.Equal(2, snap.SamplePages.Count);
        Assert.Equal("https://a/", snap.SamplePages[0].Url);
        Assert.Equal("https://b/", snap.SamplePages[1].Url);
    }

    [Fact]
    public async Task ConcurrentRecordFetchMsProducesCorrectTotal()
    {
        var acc = new DryRunAccumulator();
        using var gate = new ManualResetEventSlim(initialState: false);
        var tasks = Enumerable.Range(0, 100)
                              .Select(_ => Task.Run(() =>
                                                    {
                                                        gate.Wait();
                                                        acc.RecordFetchMs(1);
                                                    }))
                              .ToArray();
        gate.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(100, acc.Snapshot().Timings.FetchSampleCount);
        Assert.Equal(100, acc.Snapshot().Timings.TotalFetchMs);
    }

    #region Argument-validation tests

    [Fact]
    public void ConstructorThrowsWhenSamplePageLimitIsZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DryRunAccumulator(samplePageLimit: 0));
    }

    [Fact]
    public void ConstructorThrowsWhenSamplePageLimitIsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DryRunAccumulator(samplePageLimit: -1));
    }

    [Fact]
    public void RecordTotalPageThrowsOnEmptyHostKey()
    {
        var acc = new DryRunAccumulator();
        Assert.Throws<ArgumentException>(() => acc.RecordTotalPage("", depth: 0, inScope: true));
    }

    [Fact]
    public void RecordGitHubRepoThrowsOnEmptyOwnerSlashRepo()
    {
        var acc = new DryRunAccumulator();
        Assert.Throws<ArgumentException>(() => acc.RecordGitHubRepo(""));
    }

    [Fact]
    public void RecordFetchErrorThrowsOnNullError()
    {
        var acc = new DryRunAccumulator();
        Assert.Throws<ArgumentNullException>(() => acc.RecordFetchError(NullRef<DryRunFetchError>()));
    }

    [Fact]
    public void RecordSamplePageThrowsOnNullEntry()
    {
        var acc = new DryRunAccumulator();
        Assert.Throws<ArgumentNullException>(() => acc.RecordSamplePage(NullRef<DryRunPageEntry>()));
    }

    private static T NullRef<T>() where T : class
    {
        T? nullable = null;
        return Unsafe.As<T?, T>(ref nullable);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void RecordFetchMsThrowsOnNegativeValue(long ms)
    {
        var acc = new DryRunAccumulator();
        Assert.Throws<ArgumentOutOfRangeException>(() => acc.RecordFetchMs(ms));
    }

    [Fact]
    public void RecordClassifiedThrowsOnNegativeMs()
    {
        var acc = new DryRunAccumulator();
        Assert.Throws<ArgumentOutOfRangeException>(() => acc.RecordClassified(DocCategory.HowTo, classifyMs: -1));
    }

    [Fact]
    public void RecordChunkedThrowsOnNegativeMs()
    {
        var acc = new DryRunAccumulator();
        Assert.Throws<ArgumentOutOfRangeException>(() => acc.RecordChunked(chunkMs: -1));
    }

    [Fact]
    public void RecordEmbeddedBatchThrowsOnNegativeMs()
    {
        var acc = new DryRunAccumulator();
        Assert.Throws<ArgumentOutOfRangeException>(() => acc.RecordEmbeddedBatch(embedMs: -1));
    }

    #endregion

    #region Snapshot-semantics tests

    [Fact]
    public void SnapshotReturnsIsolatedCopiesThatDontReflectLaterMutations()
    {
        var acc = new DryRunAccumulator();
        acc.RecordGitHubRepo("a/a");
        acc.RecordFetchMs(10);

        var firstSnapshot = acc.Snapshot();

        acc.RecordGitHubRepo("z/z");
        acc.RecordFetchMs(99);

        Assert.Single(firstSnapshot.GitHubRepos);
        Assert.Equal("a/a", firstSnapshot.GitHubRepos[0]);
        Assert.Equal(1, firstSnapshot.Timings.FetchSampleCount);
        Assert.Equal(10, firstSnapshot.Timings.TotalFetchMs);
    }

    [Fact]
    public void GitHubReposAreReturnedInAlphabeticalOrder()
    {
        var acc = new DryRunAccumulator();
        acc.RecordGitHubRepo("zeta/z");
        acc.RecordGitHubRepo("alpha/a");
        acc.RecordGitHubRepo("mu/m");

        var snap = acc.Snapshot();

        Assert.Equal(new[] { "alpha/a", "mu/m", "zeta/z" }, snap.GitHubRepos);
    }

    [Fact]
    public void RecordTotalPageBucketsHostNamesCaseInsensitively()
    {
        var acc = new DryRunAccumulator();
        acc.RecordTotalPage("EXAMPLE.com", depth: 0, inScope: true);
        acc.RecordTotalPage("example.com", depth: 0, inScope: true);
        acc.RecordTotalPage("Example.COM", depth: 0, inScope: true);

        var snap = acc.Snapshot();

        Assert.Single(snap.PagesByHost);
        Assert.Equal(3, snap.PagesByHost.First().Value);
    }

    [Fact]
    public void RecordGitHubRepoDedupesAcrossCallsCaseInsensitively()
    {
        var acc = new DryRunAccumulator();
        acc.RecordGitHubRepo("Owner/Repo");
        acc.RecordGitHubRepo("OWNER/REPO");
        acc.RecordGitHubRepo("owner/repo");

        var snap = acc.Snapshot();

        Assert.Single(snap.GitHubRepos);
    }

    #endregion

    #region Record* coverage tests

    [Fact]
    public void RecordTotalPageUpdatesCountsAndHostAndDepthDistribution()
    {
        var acc = new DryRunAccumulator();
        acc.RecordTotalPage("https://a/", depth: 0, inScope: true);
        acc.RecordTotalPage("https://b/", depth: 1, inScope: false);
        acc.RecordTotalPage("https://a/", depth: 2, inScope: true);

        var snap = acc.Snapshot();

        Assert.Equal(3, snap.TotalPages);
        Assert.Equal(2, snap.InScopePages);
        Assert.Equal(1, snap.OutOfScopePages);
        Assert.Equal(2, snap.PagesByHost["https://a/"]);
        Assert.Equal(1, snap.PagesByHost["https://b/"]);
        Assert.Equal(1, snap.DepthDistribution[0]);
        Assert.Equal(1, snap.DepthDistribution[1]);
        Assert.Equal(1, snap.DepthDistribution[2]);
    }

    [Fact]
    public void RecordFilteredSkipAndDepthLimitedSkipBumpCounters()
    {
        var acc = new DryRunAccumulator();
        acc.RecordFilteredSkip();
        acc.RecordFilteredSkip();
        acc.RecordDepthLimitedSkip();

        var snap = acc.Snapshot();

        Assert.Equal(2, snap.FilteredSkips);
        Assert.Equal(1, snap.DepthLimitedSkips);
    }

    [Fact]
    public void RecordFetchErrorAppendsToListAndBumpsCounter()
    {
        var acc = new DryRunAccumulator();
        acc.RecordFetchError(new DryRunFetchError
                                 {
                                     Url = "https://a/",
                                     HttpStatus = 500,
                                     ErrorKind = "Http500",
                                     Message = "Server error"
                                 });
        acc.RecordFetchError(new DryRunFetchError
                                 {
                                     Url = "https://b/",
                                     HttpStatus = 0,
                                     ErrorKind = "NoResponse",
                                     Message = "No response"
                                 });

        var snap = acc.Snapshot();

        Assert.Equal(2, snap.FetchErrors);
        Assert.Equal(2, snap.Errors.Count);
        Assert.Equal("https://a/", snap.Errors[0].Url);
        Assert.Equal("Http500", snap.Errors[0].ErrorKind);
    }

    [Fact]
    public void RecordChunkedAndRecordEmbeddedBatchAggregate()
    {
        var acc = new DryRunAccumulator();
        acc.RecordChunked(10);
        acc.RecordChunked(15);
        acc.RecordEmbeddedBatch(100);

        var snap = acc.Snapshot();

        Assert.Equal(2, snap.Timings.ChunkSampleCount);
        Assert.Equal(25, snap.Timings.TotalChunkMs);
        Assert.Equal(1, snap.Timings.EmbedBatchCount);
        Assert.Equal(100, snap.Timings.TotalEmbedMs);
    }

    [Fact]
    public void RecordRenderModeStoresFinalVoterState()
    {
        var acc = new DryRunAccumulator();
        acc.RecordRenderMode(RenderMode.SPA, medianContentNodeDelta: 7, loadWaitRecommended: true);

        var snap = acc.Snapshot();

        Assert.Equal(RenderMode.SPA, snap.RenderMode);
        Assert.Equal(7, snap.MedianContentNodeDelta);
        Assert.True(snap.LoadWaitRecommended);
    }

    [Fact]
    public void RenderModeDefaultsToUnknownWhenNeverRecorded()
    {
        var acc = new DryRunAccumulator();
        var snap = acc.Snapshot();

        Assert.Equal(RenderMode.Unknown, snap.RenderMode);
        Assert.Equal(-1, snap.MedianContentNodeDelta);
        Assert.True(snap.LoadWaitRecommended);
    }

    [Fact]
    public void RecordNavigatorSwapSetsEscalatedAndReason()
    {
        var acc = new DryRunAccumulator();
        acc.RecordNavigatorSwap("React CSR detected via data-reactroot attribute");

        var snap = acc.Snapshot();

        Assert.True(snap.NavigatorEscalated);
        Assert.Equal("React CSR detected via data-reactroot attribute", snap.NavigatorEscalationReason);
    }

    [Fact]
    public void RecordNavigatorSwapEmptyReasonThrows()
    {
        var acc = new DryRunAccumulator();

        Assert.Throws<ArgumentException>(() => acc.RecordNavigatorSwap(string.Empty));
    }

    [Fact]
    public void NavigatorEscalatedDefaultsToFalseWhenNeverRecorded()
    {
        var acc = new DryRunAccumulator();
        var snap = acc.Snapshot();

        Assert.False(snap.NavigatorEscalated);
        Assert.Equal(string.Empty, snap.NavigatorEscalationReason);
    }

    #endregion

    #region Multi-mutator concurrent stress test

    [Fact]
    public async Task ConcurrentMixedMutatorsProduceCorrectAggregates()
    {
        var acc = new DryRunAccumulator();
        using var gate = new ManualResetEventSlim(initialState: false);

        var tasks = Enumerable.Range(0, 300)
                              .Select(i => Task.Run(() =>
                                                    {
                                                        gate.Wait();
                                                        switch(i % 3)
                                                        {
                                                            case 0:
                                                                acc.RecordTotalPage($"host-{i % 5}",
                                                                                    depth: i % 4,
                                                                                    inScope: true
                                                                                   );
                                                                break;
                                                            case 1:
                                                                acc.RecordFetchError(new DryRunFetchError
                                                                                         {
                                                                                             Url = $"https://err/{i}",
                                                                                             HttpStatus = 500,
                                                                                             ErrorKind = "Http500",
                                                                                             Message = "err"
                                                                                         });
                                                                break;
                                                            case 2:
                                                                acc.RecordSamplePage(new DryRunPageEntry
                                                                                         {
                                                                                             Url = $"https://s/{i}",
                                                                                             OutOfScopeDepth = 0,
                                                                                             InScope = true,
                                                                                             ContentBytes = 100,
                                                                                             LinksFound = 1,
                                                                                             ContentNodesAtDom = -1,
                                                                                             ContentNodesAtLoad = -1
                                                                                         });
                                                                break;
                                                        }
                                                    }))
                              .ToArray();
        gate.Set();
        await Task.WhenAll(tasks);

        var snap = acc.Snapshot();
        Assert.Equal(100, snap.TotalPages);
        Assert.Equal(100, snap.FetchErrors);
        Assert.Equal(100, snap.Errors.Count);
        Assert.Equal(50, snap.SamplePages.Count);
    }

    #endregion
}
