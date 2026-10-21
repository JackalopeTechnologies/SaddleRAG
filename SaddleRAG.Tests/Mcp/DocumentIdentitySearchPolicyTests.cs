// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Diagnostics;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Mcp;

public sealed class DocumentIdentitySearchPolicyTests
{
    [Theory]
    [InlineData("VF-S9 output phase loss", "VF-S9_Instruction", null)]
    [InlineData("vf-s9 output phase loss", "TOSHIBA", "manuals\\VF-S9_Instruction.pdf")]
    [InlineData("VF–S9 output phase loss", "TOSHIBA", "manuals/VF-S9_Instruction.pdf")]
    [InlineData("USB-1608 scan timing", "USB-1608 User Guide", null)]
    public void ExactNamedModelMatchesSourceMetadata(string query, string title, string? path)
    {
        var chunk = Chunk("matching", title, path);

        var matches = DocumentIdentitySearchPolicy.FindMatches(query, [chunk]);

        Assert.Contains(chunk.Id, matches);
    }

    [Theory]
    [InlineData("VF-S9 timing", "VF-S90 instructions", null)]
    [InlineData("VF-S9 timing", "AVF-S9 instructions", null)]
    [InlineData("VF-S9 timing", "TOSHIBA", "VF-S9/VF-S11.pdf")]
    [InlineData("VF-S9 and VF-S15 comparison", "VF-S9 instructions", null)]
    [InlineData("phase failure timing", "VF-S9 instructions", null)]
    [InlineData("hit-test result", "hit-test manual", null)]
    public void AmbiguousOrNonmatchingIdentityDoesNotGainPreference(string query, string title, string? path)
    {
        var chunk = Chunk("other", title, path) with { Content = "VF-S9 is mentioned in passing." };

        Assert.Empty(DocumentIdentitySearchPolicy.FindMatches(query, [chunk]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedSourceWinsWithoutDiscardingOtherEligibleCandidates(bool sorterActive)
    {
        var wanted = Chunk("wanted", "VF-S9 instruction", null);
        var other = Chunk("other", "VF-AS3 instruction", null);
        SearchTools.HybridCandidate[] candidates =
        [
            new() { Chunk = other, VectorScore = 0.9f, Bm25Score = 0, HybridScore = 0.9 },
            new() { Chunk = wanted, VectorScore = 0.8f, Bm25Score = 0, HybridScore = 0.2 }
        ];
        var sorter = Substitute.For<IReRanker>();
        sorter.ReRankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new ReRankResult { Chunk = other, RelevanceScore = 0.99f },
                        new ReRankResult { Chunk = wanted, RelevanceScore = 0.1f }]);

        var result = await SearchTools.ApplyRerankerOrPassThroughAsync(sorter, new QueryMetricsRecorder(),
            "VF-S9 timing", candidates, 2, sorterActive, 25, TestContext.Current.CancellationToken);

        Assert.Equal("wanted", result[0].Chunk.Id);
        Assert.True(result[0].SourceIdentityMatch);
        Assert.Equal("other", result[1].Chunk.Id);
        Assert.False(result[1].SourceIdentityMatch);
        Assert.Equal(0.8f, result[0].VectorScore);
        Assert.True(result[0].FinalScore > result[1].FinalScore);
    }

    [Fact]
    public async Task ZeroScoreInjectedSymbolDoesNotGainSourcePreference()
    {
        SearchTools.HybridCandidate[] candidates =
        [
            new() { Chunk = Chunk("injected", "VF-S9", null), VectorScore = 0, Bm25Score = 0, HybridScore = 0 },
            new() { Chunk = Chunk("retrieved", "Other manual", null), VectorScore = 0.5f, Bm25Score = 0, HybridScore = 0.5 }
        ];

        var result = await SearchTools.ApplyRerankerOrPassThroughAsync(Substitute.For<IReRanker>(),
            new QueryMetricsRecorder(), "VF-S9 timing", candidates, 2, false, 25, TestContext.Current.CancellationToken);

        Assert.Equal("retrieved", result[0].Chunk.Id);
        Assert.All(result, r => Assert.False(r.SourceIdentityMatch));
    }

    private static DocChunk Chunk(string id, string title, string? path) => new()
    {
        Id = id, LibraryId = "lib", Version = "published", PageUrl = $"https://example.test/{id}",
        PageTitle = title, Content = "Output phase failure timing", Category = DocCategory.HowTo,
        DocumentSource = path == null ? null : new DocumentProvenance
        {
            DocumentId = "manual", RevisionId = "revision", SourceUri = "saddlerag://manual", RelativePath = path
        }
    };
}
