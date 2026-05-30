// OnnxLlmClassifierTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;

#endregion

namespace SaddleRAG.Tests.Classification;

/// <summary>
///     Exercises <see cref="OnnxLlmClassifier" /> through a fake
///     <see cref="IClassifierGenerator" /> so the prompt-building, output
///     parsing, and failure handling are covered without a real GenAI model.
///     The classifier reuses the same prompt and parser as the Ollama
///     <see cref="LlmClassifier" />, so these tests also lock in that the two
///     backends agree on the prompt shape and the safe-default-on-failure
///     contract (Unclassified, zero confidence).
/// </summary>
public sealed class OnnxLlmClassifierTests
{
    private sealed class FakeGenerator : IClassifierGenerator
    {
        public string Response { get; set; } = string.Empty;
        public Exception? ToThrow { get; set; }
        public string? ReceivedPrompt { get; private set; }
        public int CallCount { get; private set; }

        public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        {
            ReceivedPrompt = prompt;
            CallCount++;

            Task<string> result = ToThrow != null
                                      ? Task.FromException<string>(ToThrow)
                                      : Task.FromResult(Response);

            return result;
        }
    }

    private static PageRecord NewPage(string url, string title, string content) => new()
        {
            Id = "page-1",
            LibraryId = "lib",
            Version = "v1",
            Url = url,
            Title = title,
            Category = DocCategory.Unclassified,
            RawContent = content,
            FetchedAt = DateTime.UtcNow,
            ContentHash = "hash"
        };

    private static OnnxLlmClassifier NewClassifier(IClassifierGenerator generator) =>
        new(generator, NullLogger<OnnxLlmClassifier>.Instance);

    [Fact]
    public async Task ClassifyOverviewPageReturnsOverviewCategory()
    {
        var generator = new FakeGenerator
            {
                Response = """{"category": "Overview", "confidence": 0.9}"""
            };
        var classifier = NewClassifier(generator);
        var page = NewPage("https://docs.test/about", "About", "Conceptual overview of the system.");

        var result = await classifier.ClassifyAsync(page, "lib-hint", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.Overview, result.Category);
        Assert.Equal(0.9f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public async Task ClassifyHowToPageReturnsHowToCategory()
    {
        var generator = new FakeGenerator
            {
                Response = """{"category": "HowTo", "confidence": 0.85}"""
            };
        var classifier = NewClassifier(generator);
        var page = NewPage("https://docs.test/guide", "Guide", "Step 1, step 2, step 3.");

        var result = await classifier.ClassifyAsync(page, "lib-hint", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.HowTo, result.Category);
        Assert.Equal(0.85f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public async Task ClassifyApiReferencePageHandlesFencedJson()
    {
        var generator = new FakeGenerator
            {
                Response = """
                           ```json
                           {"category": "ApiReference", "confidence": 0.7}
                           ```
                           """
            };
        var classifier = NewClassifier(generator);
        var page = NewPage("https://docs.test/api/widget", "Widget Class", "public class Widget {}");

        var result = await classifier.ClassifyAsync(page, "lib-hint", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.ApiReference, result.Category);
        Assert.Equal(0.7f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public async Task GeneratorThrowsReturnsSafeDefault()
    {
        var generator = new FakeGenerator
            {
                ToThrow = new InvalidOperationException("model exploded")
            };
        var classifier = NewClassifier(generator);
        var page = NewPage("https://docs.test/x", "X", "content");

        var result = await classifier.ClassifyAsync(page, "lib-hint", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.Unclassified, result.Category);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public async Task PassesExpectedPromptToGenerator()
    {
        var generator = new FakeGenerator
            {
                Response = """{"category": "Sample", "confidence": 0.6}"""
            };
        var classifier = NewClassifier(generator);
        var page = NewPage("https://docs.test/sample", "Sample Project", "var x = new Thing();");

        await classifier.ClassifyAsync(page, "my-library", TestContext.Current.CancellationToken);

        string expectedPrompt = ClassificationPrompt.Build(page, "my-library");
        Assert.Equal(expectedPrompt, generator.ReceivedPrompt);
        Assert.Equal(1, generator.CallCount);
    }

    [Fact]
    public async Task PromptIncludesLibraryHintAndPageMetadata()
    {
        var generator = new FakeGenerator
            {
                Response = """{"category": "Overview", "confidence": 0.5}"""
            };
        var classifier = NewClassifier(generator);
        var page = NewPage("https://docs.test/page", "The Title", "body text");

        await classifier.ClassifyAsync(page, "library-xyz", TestContext.Current.CancellationToken);

        Assert.NotNull(generator.ReceivedPrompt);
        Assert.Contains("library-xyz", generator.ReceivedPrompt);
        Assert.Contains("https://docs.test/page", generator.ReceivedPrompt);
        Assert.Contains("The Title", generator.ReceivedPrompt);
    }
}
