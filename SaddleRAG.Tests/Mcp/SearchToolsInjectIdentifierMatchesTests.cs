// SearchToolsInjectIdentifierMatchesTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Mcp.Tools;
using SaddleRAG.Tests.Monitor;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class SearchToolsInjectIdentifierMatchesTests
{
    [Fact]
    public async Task InjectIdentifierMatchesAsyncPrependsExactCaseInsensitiveMatch()
    {
        var hybrid = new[] { MakeHybridCandidate("existing-1", "Unrelated") };
        var fakeRepo = new FakeChunkRepository();
        var matchChunk = MakeChunk("symbol-chunk", "MovePvt");
        fakeRepo.SetQualifiedNameMatches("MovePVT", [matchChunk]);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePVT",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.Equal(2, result.Count);
        Assert.Equal("symbol-chunk", result[0].Chunk.Id);
    }

    [Fact]
    public async Task InjectIdentifierMatchesAsyncRejectsSubstringNearMisses()
    {
        var hybrid = new[] { MakeHybridCandidate("existing-1", "Unrelated") };
        var fakeRepo = new FakeChunkRepository();
        var substringNearMiss = MakeChunk("substring-chunk", "MovePvtExtended");
        fakeRepo.SetQualifiedNameMatches("MovePvt", [substringNearMiss]);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePvt",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.Single(result);
        Assert.Equal("existing-1", result[0].Chunk.Id);
    }

    [Fact]
    public async Task InjectIdentifierMatchesAsyncDedupesChunksAlreadyInHybridPool()
    {
        var hybrid = new[] { MakeHybridCandidate("shared-chunk", "MovePvt") };
        var fakeRepo = new FakeChunkRepository();
        var duplicate = MakeChunk("shared-chunk", "MovePvt");
        fakeRepo.SetQualifiedNameMatches("MovePvt", [duplicate]);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePvt",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.Single(result);
        Assert.Equal("shared-chunk", result[0].Chunk.Id);
    }

    [Fact]
    public async Task InjectIdentifierMatchesAsyncCapsInjectedCountAtFive()
    {
        var hybrid = new[] { MakeHybridCandidate("hybrid-1", "Unrelated") };
        var fakeRepo = new FakeChunkRepository();
        var many = Enumerable.Range(1, 12)
                             .Select(i => MakeChunk($"injected-{i}", "Disabled"))
                             .ToList();
        fakeRepo.SetQualifiedNameMatches("Disabled", many);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "Disabled",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        // 5 injected (capped) + 1 hybrid = 6 total
        Assert.Equal(6, result.Count);
        Assert.Equal("hybrid-1", result[^1].Chunk.Id);
    }

    [Fact]
    public async Task InjectIdentifierMatchesAsyncProducesZeroScoresForInjectedRowsSoRerankIsTheOnlySignal()
    {
        var hybrid = Array.Empty<object>().Select(_ => MakeHybridCandidate("", "")).ToList();
        var fakeRepo = new FakeChunkRepository();
        fakeRepo.SetQualifiedNameMatches("MovePvt", [MakeChunk("symbol-chunk", "MovePvt")]);

        var result = await SearchTools.InjectIdentifierMatchesAsync(hybrid,
                                                                    fakeRepo,
                                                                    "MovePvt",
                                                                    "lib",
                                                                    "1.0",
                                                                    TestContext.Current.CancellationToken
                                                                   );

        var injected = result.Single();
        Assert.Equal(expected: 0f, injected.VectorScore);
        Assert.Equal(expected: 0.0, injected.Bm25Score);
        Assert.Equal(expected: 0.0, injected.HybridScore);
    }

    private static SearchTools.HybridCandidate MakeHybridCandidate(string id, string qualifiedName) =>
        new SearchTools.HybridCandidate
            {
                Chunk = MakeChunk(id, qualifiedName),
                VectorScore = 0.5f,
                Bm25Score = 0.5,
                HybridScore = 0.5
            };

    private static DocChunk MakeChunk(string id, string qualifiedName) =>
        new DocChunk
            {
                Id = id,
                LibraryId = "lib",
                Version = "1.0",
                PageUrl = "https://example.com",
                PageTitle = "Page",
                Category = DocCategory.ApiReference,
                Content = "content",
                QualifiedName = qualifiedName
            };
}
