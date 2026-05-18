// DryRunAccumulatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

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
}
