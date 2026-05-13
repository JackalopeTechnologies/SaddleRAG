// McpWarmupServiceRerankProbeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Locks in the rerank-warmup contract: <c>McpWarmupService</c> must
///     invoke <see cref="IReRanker.ReRankAsync" /> exactly once at startup,
///     with at least one candidate chunk, so the ONNX cross-encoder
///     session pays its cold-path cost (~6 s on CPU per Phase 4 testing)
///     during warmup instead of on the first user query. If anyone
///     removes the probe call, these tests catch it before the latency
///     regression ships.
/// </summary>
public sealed class McpWarmupServiceRerankProbeTests
{
    [Fact]
    public async Task WarmReRankerAsyncInvokesRerankerOnce()
    {
        var fake = new CountingReRanker();

        await McpWarmupService.WarmReRankerAsync(fake, CancellationToken.None);

        Assert.Equal(expected: 1, fake.CallCount);
    }

    [Fact]
    public async Task WarmReRankerAsyncSuppliesAtLeastOneCandidate()
    {
        var fake = new CountingReRanker();

        await McpWarmupService.WarmReRankerAsync(fake, CancellationToken.None);

        Assert.NotNull(fake.LastCandidates);
        Assert.NotEmpty(fake.LastCandidates);
    }

    [Fact]
    public async Task WarmReRankerAsyncSuppliesNonEmptyQueryString()
    {
        var fake = new CountingReRanker();

        await McpWarmupService.WarmReRankerAsync(fake, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(fake.LastQuery));
    }

    [Fact]
    public async Task WarmReRankerAsyncPropagatesCancellation()
    {
        var fake = new CountingReRanker();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => McpWarmupService.WarmReRankerAsync(fake, cts.Token)
        );
    }

    /// <summary>
    ///     Test double for <see cref="IReRanker" /> that records what
    ///     <see cref="McpWarmupService.WarmReRankerAsync" /> passes in.
    ///     Throws on a cancelled token to match the real reranker's
    ///     <c>ct.ThrowIfCancellationRequested()</c> contract.
    /// </summary>
    private sealed class CountingReRanker : IReRanker
    {
        public int CallCount { get; private set; }
        public string? LastQuery { get; private set; }
        public IReadOnlyList<DocChunk>? LastCandidates { get; private set; }

        public Task<IReadOnlyList<ReRankResult>> ReRankAsync(string query,
                                                             IReadOnlyList<DocChunk> candidates,
                                                             int maxResults,
                                                             CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            LastQuery = query;
            LastCandidates = candidates;

            IReadOnlyList<ReRankResult> result = candidates
                .Select(c => new ReRankResult { Chunk = c, RelevanceScore = SyntheticScore })
                .ToList();
            return Task.FromResult(result);
        }

        private const float SyntheticScore = 0.5f;
    }
}
