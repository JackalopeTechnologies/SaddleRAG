// OllamaReRankerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Unit tests for the pure-function pieces of OllamaReRanker:
///     BuildPrompt and ParseScore. Covers the parser's tolerance to
///     malformed model output, the prompt's structural invariants, and
///     argument validation. Integration coverage (real Ollama call) is
///     a separate concern with a Category=Integration trait so it can be
///     skipped on CI machines without an Ollama daemon.
/// </summary>
public sealed class OllamaReRankerTests
{
    #region ParseScore

    [Theory]
    [InlineData("0.85", 0.85f)]
    [InlineData("0.0", 0.0f)]
    [InlineData("1.0", 1.0f)]
    [InlineData("0.5", 0.5f)]
    [InlineData("0", 0.0f)]
    [InlineData("1", 1.0f)]
    [InlineData(".97", 0.97f)]
    public void ParseScoreExtractsCleanFloat(string input, float expected)
    {
        var result = OllamaReRanker.ParseScore(input);

        Assert.Equal(expected, result, precision: 4);
    }

    [Theory]
    [InlineData("Relevance: 0.85", 0.85f)]
    [InlineData("Score: 0.42", 0.42f)]
    [InlineData("The score is 0.7.", 0.7f)]
    [InlineData("0.85 (high confidence)", 0.85f)]
    [InlineData("0.92 — strong match", 0.92f)]
    public void ParseScoreToleratesNoiseAroundNumber(string input, float expected)
    {
        var result = OllamaReRanker.ParseScore(input);

        Assert.Equal(expected, result, precision: 4);
    }

    [Theory]
    [InlineData("1.5", 1.0f)]
    [InlineData("2", 1.0f)]
    [InlineData("99.9", 1.0f)]
    public void ParseScoreClampsOutOfRangeToOne(string input, float expected)
    {
        var result = OllamaReRanker.ParseScore(input);

        Assert.Equal(expected, result, precision: 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("irrelevant")]
    [InlineData("not a number here")]
    public void ParseScoreReturnsZeroWhenNoFloatPresent(string input)
    {
        var result = OllamaReRanker.ParseScore(input);

        Assert.Equal(expected: 0f, result);
    }

    [Fact]
    public void ParseScoreExtractsFirstFloatWhenMultiplePresent()
    {
        var result = OllamaReRanker.ParseScore("0.85 then 0.50 then 0.20");

        Assert.Equal(expected: 0.85f, result, precision: 4);
    }

    #endregion

    #region BuildPrompt

    [Fact]
    public void BuildPromptIncludesQuery()
    {
        var prompt = OllamaReRanker.BuildPrompt("FastLineRenderableSeries", "doc body");

        Assert.Contains("Query: FastLineRenderableSeries", prompt);
    }

    [Fact]
    public void BuildPromptIncludesDocument()
    {
        var prompt = OllamaReRanker.BuildPrompt("query", "specific document text 12345");

        Assert.Contains("specific document text 12345", prompt);
    }

    [Fact]
    public void BuildPromptIncludesScoreInstruction()
    {
        var prompt = OllamaReRanker.BuildPrompt("q", "d");

        Assert.Contains("0.0", prompt);
        Assert.Contains("1.0", prompt);
        Assert.EndsWith("Score:", prompt);
    }

    [Fact]
    public void BuildPromptContainsFewShotAnchors()
    {
        var prompt = OllamaReRanker.BuildPrompt("q", "d");

        Assert.Contains("Score: 0.95", prompt);
        Assert.Contains("Score: 0.55", prompt);
        Assert.Contains("Score: 0.05", prompt);
    }

    [Fact]
    public void BuildPromptForbidsExplanation()
    {
        var prompt = OllamaReRanker.BuildPrompt("q", "d");

        Assert.Contains("ONLY", prompt);
        Assert.Contains("No explanation", prompt);
    }

    [Fact]
    public void BuildPromptThrowsForEmptyQuery()
    {
        Assert.Throws<ArgumentException>(() => OllamaReRanker.BuildPrompt(string.Empty, "doc"));
    }

    #endregion
}
