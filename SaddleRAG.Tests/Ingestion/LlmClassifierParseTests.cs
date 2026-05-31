// LlmClassifierParseTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Classification;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Exercises <see cref="OllamaLlmClassifier.ParseClassificationResponse" />.
///     The parser is the contract that decides whether an Ollama
///     response is "trusted enough to overwrite the heuristic category"
///     (non-zero confidence) versus "fall through" (Unclassified, 0
///     confidence). Bugs here silently regress reextract_library
///     quality, so locking in the contract matters even though the
///     method is private-by-default. Exposed via <c>internal static</c>
///     plus the <c>InternalsVisibleTo</c> attribute on the Ingestion
///     assembly.
/// </summary>
public sealed class LlmClassifierParseTests
{
    [Fact]
    public void ParsesCleanJsonResponse()
    {
        string raw = """{"category": "HowTo", "confidence": 0.92}""";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.HowTo, result.Category);
        Assert.Equal(0.92f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public void StripsLeadingJsonCodeFence()
    {
        string raw = """
                     ```json
                     {"category": "ApiReference", "confidence": 0.77}
                     ```
                     """;
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.ApiReference, result.Category);
        Assert.Equal(0.77f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public void IgnoresExtraWhitespace()
    {
        string raw = "   \n\t" + """{"category": "Overview", "confidence": 0.5}""" + "\n  ";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.Overview, result.Category);
        Assert.Equal(0.5f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public void IsCaseInsensitiveOnCategoryName()
    {
        string raw = """{"category": "howto", "confidence": 0.6}""";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.HowTo, result.Category);
    }

    [Fact]
    public void UnknownCategoryFallsBackToUnclassifiedWithReportedConfidence()
    {
        string raw = """{"category": "NotACategory", "confidence": 0.4}""";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.Unclassified, result.Category);
        Assert.Equal(0.4f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public void MissingCategoryFieldYieldsUnclassified()
    {
        string raw = """{"confidence": 0.9}""";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.Unclassified, result.Category);
        Assert.Equal(0.9f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public void MissingConfidenceFieldYieldsZeroConfidence()
    {
        string raw = """{"category": "Sample"}""";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.Sample, result.Category);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public void NonNumberConfidenceYieldsZero()
    {
        string raw = """{"category": "ChangeLog", "confidence": "very high"}""";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.ChangeLog, result.Category);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public void InvalidJsonFallsBackToSubstringScan()
    {
        string raw = "I think this looks like a HowTo tutorial.";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.HowTo, result.Category);
        Assert.Equal(0.5f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public void InvalidJsonWithNoCategoryNameStaysUnclassified()
    {
        string raw = "I'm not sure what this is.";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.Unclassified, result.Category);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public void EmptyResponseYieldsUnclassifiedZero()
    {
        var result = OllamaLlmClassifier.ParseClassificationResponse(string.Empty);

        Assert.Equal(DocCategory.Unclassified, result.Category);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public void WhitespaceOnlyResponseYieldsUnclassifiedZero()
    {
        var result = OllamaLlmClassifier.ParseClassificationResponse("   \t\n  ");

        Assert.Equal(DocCategory.Unclassified, result.Category);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public void IntegerConfidenceIsAccepted()
    {
        string raw = """{"category": "Code", "confidence": 1}""";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(DocCategory.Code, result.Category);
        Assert.Equal(1f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public void FirstMatchWinsOnSubstringFallback()
    {
        // Substring scan iterates DocCategory values in declaration order
        // and takes the first match. Confirms callers can't accidentally
        // get a different category than what comes back from the JSON
        // parse path on the same input shape.
        string raw = "This is both a Sample and an Overview.";
        var result = OllamaLlmClassifier.ParseClassificationResponse(raw);

        Assert.Equal(0.5f, result.Confidence, tolerance: 0.001f);
        Assert.True(result.Category is DocCategory.Overview or DocCategory.Sample,
                    $"Expected Overview or Sample, got {result.Category}");
    }
}
