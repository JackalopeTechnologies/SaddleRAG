// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Diagnostics;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Mcp;

public sealed class SearchRankFusionTests
{
    [Fact]
    public void StrongSemanticAnswerSurvivesRepetitiveKeywordMatches()
    {
        var vectors = Enumerable.Range(1, 50).Select(i => Vector($"c{i}", 0.8f - i * 0.001f)).ToArray();
        var keywords = vectors.ToDictionary(v => v.Chunk.Id, v => v.Chunk.Id == "c1" ? 0.01 : 100.0);

        var result = SearchTools.BlendVectorAndBm25(vectors, keywords, 0.4f, null);

        Assert.Contains(result.Take(2), r => r.Chunk.Id == "c1");
        Assert.Equal(vectors[0].Score, result.Single(r => r.Chunk.Id == "c1").VectorScore);
    }

    [Fact]
    public void StrongKeywordAnswerSurvivesLowSemanticPosition()
    {
        var vectors = Enumerable.Range(1, 50).Select(i => Vector($"c{i}", 0.8f - i * 0.001f)).ToArray();
        var keywords = new Dictionary<string, double> { ["c40"] = 100, ["c1"] = 1 };

        var result = SearchTools.BlendVectorAndBm25(vectors, keywords, 0.4f, null);

        Assert.Contains(result.Take(3), r => r.Chunk.Id == "c40");
    }

    [Fact]
    public void UnretrievedKeywordMatchesDoNotChangeEligibleOrdering()
    {
        VectorSearchResult[] vectors = [Vector("a", 0.8f), Vector("b", 0.7f), Vector("c", 0.6f)];
        var keywords = new Dictionary<string, double> { ["c"] = 3, ["b"] = 2 };
        var before = SearchTools.BlendVectorAndBm25(vectors, keywords, 0.4f, null);
        keywords["excluded"] = 1_000_000;

        var after = SearchTools.BlendVectorAndBm25(vectors, keywords, 0.4f, null);

        Assert.Equal(before.Select(r => (r.Chunk.Id, r.HybridScore)), after.Select(r => (r.Chunk.Id, r.HybridScore)));
        Assert.DoesNotContain(after, r => r.Chunk.Id == "excluded");
    }

    [Fact]
    public void TiedScoresGiveEqualVotesAndMissingKeywordsGiveNone()
    {
        VectorSearchResult[] vectors = [Vector("a", 0.8f), Vector("b", 0.8f), Vector("c", 0.8f)];
        var keywords = new Dictionary<string, double> { ["a"] = 10, ["b"] = 10, ["c"] = 0 };

        var result = SearchTools.BlendVectorAndBm25(vectors, keywords, 0.4f, null);

        Assert.Equal(result[0].HybridScore, result[1].HybridScore);
        Assert.True(result[2].HybridScore < result[1].HybridScore);
        Assert.Equal("c", result[2].Chunk.Id);
    }

    [Fact]
    public void InferredSubjectStillBreaksCloseSemanticMatches()
    {
        VectorSearchResult[] vectors =
        [
            Vector("other", 0.82f),
            new() { Chunk = Chunk("matching") with { SubjectIds = ["hydraulics"] }, Score = 0.80f }
        ];

        var result = SearchTools.BlendVectorAndBm25(vectors, new Dictionary<string, double>(), 0.4f, "hydraulics");

        Assert.Equal("matching", result[0].Chunk.Id);
        Assert.True(result[0].SubjectBoost > 0);
    }

    [Fact]
    public async Task DisagreeingSorterCannotEraseRetrievalLeader()
    {
        var candidates = Candidates(50);
        var sorter = Substitute.For<IReRanker>();
        sorter.ReRankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(call => (call.Arg<IReadOnlyList<DocChunk>>() ?? []).Reverse()
                  .Select((chunk, i) => new ReRankResult { Chunk = chunk, RelevanceScore = 1f / (i + 1) }).ToArray());

        var result = await Rank(sorter, candidates, active: true);

        Assert.Contains(result.Take(2), r => r.Chunk.Id == "c1");
        Assert.Contains(result.Take(2), r => r.Chunk.Id == "c25");
        Assert.All(result, r => Assert.NotNull(r.RerankScore));
        await sorter.Received(1).ReRankAsync("query", Arg.Is<IReadOnlyList<DocChunk>>(c => c != null && c.Count == 25), 25,
                                           TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MissingSorterRowsDoNotLoseCandidatesOrAdmitForeignContent()
    {
        var candidates = Candidates(5);
        var sorter = Substitute.For<IReRanker>();
        sorter.ReRankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns([new ReRankResult { Chunk = Chunk("foreign"), RelevanceScore = 1 }]);

        var result = await Rank(sorter, candidates, active: true);

        Assert.Equal(candidates.Select(c => c.Chunk.Id), result.Select(c => c.Chunk.Id));
        Assert.All(result, r => Assert.Null(r.RerankScore));
    }

    [Fact]
    public async Task DisabledSorterDoesNotPromotePrependedZeroScoreSymbolMatches()
    {
        var candidates = SearchTools.InjectIdentifierMatches(Candidates(5), [Chunk("symbol")]);
        Assert.Equal("symbol", candidates[0].Chunk.Id);

        var result = await Rank(Substitute.For<IReRanker>(), candidates, active: false);

        Assert.Equal("c1", result[0].Chunk.Id);
        Assert.DoesNotContain(result, r => r.Chunk.Id == "symbol");
    }

    [Fact]
    public async Task DisabledSorterPreservesRetrievalAndDoesNoModelWork()
    {
        var candidates = Candidates(5);
        var sorter = Substitute.For<IReRanker>();

        var result = await Rank(sorter, candidates, active: false);

        Assert.Equal(candidates.Select(c => c.Chunk.Id), result.Select(c => c.Chunk.Id));
        Assert.All(result, r => Assert.Null(r.RerankScore));
        await sorter.DidNotReceiveWithAnyArgs().ReRankAsync(default!, default!, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateWebPassageCannotCrowdOutFifthDistinctAnswer(bool active)
    {
        var candidates = Candidates(6);
        candidates[4] = candidates[4] with
            { Chunk = candidates[3].Chunk with { Id = "duplicate", PageUrl = "https://example.test/c4/index" } };
        var sorter = OrderedSorter();

        var result = await Rank(sorter, candidates, active);

        Assert.Equal(new[] { "c1", "c2", "c3", "c4", "c6" }, result.Select(r => r.Chunk.Id));
        Assert.Equal("https://example.test/c4", result[3].Chunk.PageUrl);
        Assert.Equal(candidates[3].Chunk.Content, result[3].Chunk.Content);
        Assert.Equal(active, result[3].RerankScore.HasValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EqualProseInDifferentTechnicalContextsRemainsDistinct(bool active)
    {
        var original = Chunk("original");
        var source = new DocumentProvenance
            { DocumentId = "manual", RevisionId = "revision", SourceUri = "file:///manual.pdf", RelativePath = "manual.pdf" };
        DocChunk[] chunks =
        [
            original,
            original with { Id = "library", LibraryId = "another-library" },
            original with { Id = "version", Version = "v2" },
            original with { Id = "category", Category = DocCategory.HowTo },
            original with { Id = "title", PageTitle = "another title" },
            original with { Id = "section", SectionPath = "another section" },
            original with { Id = "symbol", QualifiedName = "Another.Symbol" },
            original with { Id = "language", CodeLanguage = "csharp" },
            original with { Id = "document", DocumentSource = source },
            original with { Id = "revision", DocumentSource = source with { RevisionId = "other" } },
            original with { Id = "page", DocumentSource = source with { PageStart = 2 } },
            original with { Id = "text", Content = "original with an additional qualification" }
        ];
        var candidates = chunks.Select((chunk, i) => new SearchTools.HybridCandidate
            { Chunk = chunk, HybridScore = 1.0 / (i + 1), VectorScore = 0.8f, Bm25Score = 0 }).ToArray();

        var result = await SearchTools.ApplyRerankerOrPassThroughAsync(OrderedSorter(), new QueryMetricsRecorder(),
            "query", candidates, chunks.Length, active, 25, TestContext.Current.CancellationToken);

        Assert.Equal(chunks.Select(c => c.Id), result.Select(r => r.Chunk.Id));
    }

    private static IReRanker OrderedSorter()
    {
        var sorter = Substitute.For<IReRanker>();
        sorter.ReRankAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(call => (call.Arg<IReadOnlyList<DocChunk>>() ?? []).Select((chunk, i) =>
                  new ReRankResult { Chunk = chunk, RelevanceScore = 1f / (i + 1) }).ToArray());
        return sorter;
    }

    private static Task<IReadOnlyList<SearchTools.RankedResult>> Rank(IReRanker sorter,
        IReadOnlyList<SearchTools.HybridCandidate> candidates, bool active) =>
        SearchTools.ApplyRerankerOrPassThroughAsync(sorter, new QueryMetricsRecorder(), "query", candidates,
                                                   5, active, 25, TestContext.Current.CancellationToken);

    private static SearchTools.HybridCandidate[] Candidates(int count) =>
        Enumerable.Range(1, count).Select(i => new SearchTools.HybridCandidate
            { Chunk = Chunk($"c{i}"), HybridScore = 1.0 / i, VectorScore = 0.8f, Bm25Score = 0 }).ToArray();

    private static VectorSearchResult Vector(string id, float score) => new() { Chunk = Chunk(id), Score = score };

    private static DocChunk Chunk(string id) => new()
        {
            Id = id, LibraryId = "lib", Version = "v1", PageUrl = $"https://example.test/{id}",
            PageTitle = id, Content = id, Category = DocCategory.ApiReference
        };
}
