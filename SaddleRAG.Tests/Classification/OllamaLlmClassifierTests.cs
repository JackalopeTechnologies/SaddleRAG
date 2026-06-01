// OllamaLlmClassifierTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OllamaSharp.Models;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Classification;

/// <summary>
///     Exercises <see cref="OllamaLlmClassifier" /> through a fake
///     <see cref="IOllamaGenerateClient" /> so the prompt-building, output
///     parsing, and failure handling are covered without a live Ollama endpoint.
///     Mirrors the shape of <see cref="OnnxLlmClassifierTests" />.
/// </summary>
public sealed class OllamaLlmClassifierTests
{
    private sealed class FakeGenerateClient : IOllamaGenerateClient
    {
        public string Response { get; set; } = string.Empty;
        public Exception? ToThrow { get; set; }
        public GenerateRequest? ReceivedRequest { get; private set; }

        public async IAsyncEnumerable<GenerateResponseStream?> GenerateAsync(
            GenerateRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ReceivedRequest = request;

            if (ToThrow != null)
                throw ToThrow;

            yield return new GenerateResponseStream { Response = Response };

            await Task.CompletedTask;
        }
    }

    private static OllamaSettings MakeSettings() =>
        new()
        {
            ClassificationModels =
            {
                new OllamaModelEntry { Name = "test-classifier:latest" }
            }
        };

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

    private static OllamaLlmClassifier NewClassifier(FakeGenerateClient client) =>
        new(Options.Create(MakeSettings()),
            client,
            NullLogger<OllamaLlmClassifier>.Instance);

    [Fact]
    public async Task ClassifyOverviewPageReturnsOverviewCategory()
    {
        var client = new FakeGenerateClient
            {
                Response = """{"category": "Overview", "confidence": 0.9}"""
            };
        var classifier = NewClassifier(client);
        var page = NewPage("https://docs.test/about", "About", "Conceptual overview of the system.");

        var result = await classifier.ClassifyAsync(page, "lib-hint", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.Overview, result.Category);
        Assert.Equal(0.9f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public async Task ClassifyHowToPageReturnsHowToCategory()
    {
        var client = new FakeGenerateClient
            {
                Response = """{"category": "HowTo", "confidence": 0.85}"""
            };
        var classifier = NewClassifier(client);
        var page = NewPage("https://docs.test/guide", "Guide", "Step 1, step 2, step 3.");

        var result = await classifier.ClassifyAsync(page, "lib-hint", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.HowTo, result.Category);
        Assert.Equal(0.85f, result.Confidence, tolerance: 0.001f);
    }

    [Fact]
    public async Task OllamaCallThrowsReturnsSafeDefault()
    {
        var client = new FakeGenerateClient
            {
                ToThrow = new InvalidOperationException("Ollama not running")
            };
        var classifier = NewClassifier(client);
        var page = NewPage("https://docs.test/x", "X", "content");

        var result = await classifier.ClassifyAsync(page, "lib-hint", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.Unclassified, result.Category);
        Assert.Equal(0f, result.Confidence);
    }

    [Fact]
    public async Task PassesExpectedPromptToOllama()
    {
        var client = new FakeGenerateClient
            {
                Response = """{"category": "Sample", "confidence": 0.6}"""
            };
        var classifier = NewClassifier(client);
        var page = NewPage("https://docs.test/sample", "Sample Project", "var x = new Thing();");

        await classifier.ClassifyAsync(page, "my-library", TestContext.Current.CancellationToken);

        string expectedPrompt = ClassificationPrompt.Build(page, "my-library");
        Assert.NotNull(client.ReceivedRequest);
        if (client.ReceivedRequest != null)
            Assert.Equal(expectedPrompt, client.ReceivedRequest.Prompt);
    }
}
