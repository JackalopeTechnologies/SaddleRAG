// Bm25ScorerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class Bm25ScorerTests
{
    [Fact]
    public async Task EmptyIndexProducesEmptyScores()
    {
        var build = Bm25IndexBuilder.Build("lib", "1.0", []);
        var lookup = new InMemoryBm25TermLookup(build);

        var scores =
            await Bm25Scorer.ScoreAsync(lookup, build.Stats, "anything", TestContext.Current.CancellationToken);

        Assert.Empty(scores);
    }

    [Fact]
    public async Task IdentifierQueryHitsChunksContainingIdentifier()
    {
        var chunks = new[]
                         {
                             MakeChunk("a", "Use MoveLinear to start the move."),
                             MakeChunk("b", "Coordinate motion is described elsewhere."),
                             MakeChunk("c", "MoveLinear parameters control velocity.")
                         };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);
        var scores =
            await Bm25Scorer.ScoreAsync(lookup, build.Stats, "MoveLinear", TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, scores.Count);
        Assert.True(scores.ContainsKey("a"));
        Assert.True(scores.ContainsKey("c"));
        Assert.False(scores.ContainsKey("b"));
    }

    [Fact]
    public async Task ProseQueryMatchesLowercasedTokens()
    {
        var chunks = new[]
                         {
                             MakeChunk("a", "Configure homing on each axis."),
                             MakeChunk("b", "Configure encoder feedback.")
                         };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);
        var scores = await Bm25Scorer.ScoreAsync(lookup,
                                                 build.Stats,
                                                 "homing configuration",
                                                 TestContext.Current.CancellationToken
                                                );

        Assert.NotEmpty(scores);
        Assert.True(scores["a"] > scores.GetValueOrDefault("b", defaultValue: 0.0));
    }

    [Fact]
    public async Task TopNReturnsHighestScoringChunksInOrder()
    {
        var chunks = new[]
                         {
                             MakeChunk("a", "MoveLinear once."),
                             MakeChunk("b", "MoveLinear MoveLinear twice."),
                             MakeChunk("c", "MoveLinear MoveLinear MoveLinear three times.")
                         };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);
        var topTwo = await Bm25Scorer.TopNAsync(lookup,
                                                build.Stats,
                                                "MoveLinear",
                                                n: 2,
                                                TestContext.Current.CancellationToken
                                               );

        Assert.Equal(expected: 2, topTwo.Count);
        Assert.Contains(topTwo, t => t.ChunkId == "c");
    }

    [Fact]
    public void ExtractQueryTermsEmitsLowercaseVariantOfDottedIdentifier()
    {
        var terms = Bm25Scorer.ExtractQueryTerms("AxisFault.Disabled");

        Assert.Contains("AxisFault.Disabled", terms);
        Assert.Contains("axisfault.disabled", terms);
    }

    [Fact]
    public async Task CaseMismatchedDottedIdentifierQueryScoresChunkContainingMixedCase()
    {
        var chunks = new[]
                         {
                             MakeChunk("a", "Set AxisFault.Disabled to acknowledge."),
                             MakeChunk("b", "Other unrelated content here.")
                         };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);
        var scores = await Bm25Scorer.ScoreAsync(lookup,
                                                 build.Stats,
                                                 "axisfault.disabled",
                                                 TestContext.Current.CancellationToken
                                                );

        Assert.True(scores.ContainsKey("a"), "case-mismatched dotted identifier should still score the matching chunk");
    }

    [Fact]
    public async Task DottedIdentifierQueryMatchesDottedIdentifier()
    {
        var chunks = new[]
                         {
                             MakeChunk("a", "Set AxisFault.Disabled to acknowledge."),
                             MakeChunk("b", "Other unrelated content here.")
                         };

        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);
        var scores = await Bm25Scorer.ScoreAsync(lookup,
                                                 build.Stats,
                                                 "AxisFault.Disabled",
                                                 TestContext.Current.CancellationToken
                                                );

        Assert.NotEmpty(scores);
        Assert.True(scores.ContainsKey("a"));
    }

    [Fact]
    public async Task ConnectingWordsHaveLessInfluenceWithoutDiscardingEvidence()
    {
        var chunks = new[]
        {
            MakeChunk("answer", "Configure return values for any arguments."),
            MakeChunk("unrelated", "Learn about hosting and configuring services.")
        };
        var build = Bm25IndexBuilder.Build("lib", "1.0", chunks);
        var lookup = new InMemoryBm25TermLookup(build);

        var scores = await Bm25Scorer.ScoreAsync(lookup, build.Stats, "Tell me about return values",
            TestContext.Current.CancellationToken);
        var literal = await Bm25Scorer.ScoreAsync(lookup, build.Stats, "Tell me `about` return values",
            TestContext.Current.CancellationToken);

        Assert.Contains("answer", scores.Keys);
        Assert.True(scores["unrelated"] > 0);
        Assert.True(scores["unrelated"] < literal["unrelated"]);
        Assert.Equal(literal["answer"], scores["answer"]);
    }

    [Theory]
    [InlineData("about")]
    [InlineData("about the")]
    [InlineData("Explain the `about` parameter")]
    [InlineData("Explain the \"about\" parameter")]
    [InlineData("Explain the 'about' parameter")]
    [InlineData("How does About() work?")]
    public async Task LiteralAndStandaloneConnectingWordsRetainFullWeight(string query)
    {
        var build = Bm25IndexBuilder.Build("lib", "1.0", [MakeChunk("answer", "About")]);
        var lookup = new InMemoryBm25TermLookup(build);
        var scores = await Bm25Scorer.ScoreAsync(lookup, build.Stats, query, TestContext.Current.CancellationToken);
        var baseline = await Bm25Scorer.ScoreAsync(lookup, build.Stats,
            query.Contains("About", StringComparison.Ordinal) ? "About" : "about", TestContext.Current.CancellationToken);

        Assert.Equal(baseline["answer"], scores["answer"]);
    }

    [Fact]
    public void QueryFilteringPreservesTechnicalIntentAndQualifiedNames()
    {
        var terms = Bm25Scorer.ExtractQueryTerms(
            "How does Widget.About return any value for all arguments without matching not null?");

        Assert.Contains("how", terms);
        Assert.Contains("for", terms);
        Assert.Contains("Widget.About", terms);
        Assert.Contains("widget.about", terms);
        Assert.All(new[] { "return", "any", "value", "all", "arguments", "without", "matching", "not", "null" },
            term => Assert.Contains(term, terms));
    }

    private static DocChunk MakeChunk(string id, string content) =>
        new DocChunk
            {
                Id = id,
                LibraryId = "lib",
                Version = "1.0",
                PageUrl = "https://example.com",
                PageTitle = "Page",
                Category = DocCategory.ApiReference,
                Content = content
            };
}
