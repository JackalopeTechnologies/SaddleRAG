// OllamaLlmClassifierTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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
        public IReadOnlyList<string>? Chunks { get; set; }
        public Exception? ToThrow { get; set; }
        public GenerateRequest? ReceivedRequest { get; private set; }
        public bool ReadPastAnswer { get; private set; }
        public int ReadChunks { get; private set; }

        public async IAsyncEnumerable<GenerateResponseStream?> GenerateAsync(
            GenerateRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ReceivedRequest = request;

            if (ToThrow != null)
                throw ToThrow;

            ct.ThrowIfCancellationRequested();
            foreach(string chunk in Chunks ?? [Response])
            {
                ReadChunks++;
                yield return new GenerateResponseStream { Response = chunk };
            }

            ReadPastAnswer = true;

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
    public async Task StructuredGenerationRequestsJsonWithDeterministicSampling()
    {
        const string reply = "{\"concepts\":[]}";
        var client = new FakeGenerateClient { Response = reply };
        var classifier = NewClassifier(client);

        string result = await classifier.GenerateAsync("Return a subject catalog as JSON.",
                                                       TestContext.Current.CancellationToken);

        Assert.Equal(reply, result);
        Assert.NotNull(client.ReceivedRequest);
        Assert.Equal("json", client.ReceivedRequest.Format);
        Assert.NotNull(client.ReceivedRequest.Options);
        Assert.Equal(0f, client.ReceivedRequest.Options.Temperature);
        Assert.False(client.ReadPastAnswer);
    }

    [Fact]
    public async Task StructuredGenerationPassesTheSchemaAndStopsWhenTheAnswerIsComplete()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"subject":{"type":"string","maxLength":256}},"required":["subject"]}""");
        const string reply = """{"subject":"motor"}""";
        var client = new FakeGenerateClient { Response = reply };
        var classifier = NewClassifier(client);

        string result = await classifier.GenerateAsync("Classify a motor datasheet.", schema.RootElement,
            TestContext.Current.CancellationToken);

        Assert.Equal(reply, result);
        Assert.NotNull(client.ReceivedRequest);
        JsonElement sentSchema = Assert.IsType<JsonElement>(client.ReceivedRequest.Format);
        Assert.Equal(schema.RootElement.GetRawText(), sentSchema.GetRawText());
        Assert.False(client.ReadPastAnswer);
    }

    [Fact]
    public async Task ClosingBraceInsideAStringDoesNotEndAnIncompleteAnswer()
    {
        var client = new FakeGenerateClient { Chunks = ["{\"subject\":\"motor }", " datasheet\"}", "discarded trailing text"] };
        var classifier = NewClassifier(client);

        string result = await classifier.GenerateAsync("Return JSON.", TestContext.Current.CancellationToken);

        Assert.Equal("{\"subject\":\"motor } datasheet\"}", result);
        Assert.Equal(2, client.ReadChunks);
        Assert.False(client.ReadPastAnswer);
    }

    [Fact]
    public async Task OversizedGenerationFailsWithoutConsumingTheRestOfTheStream()
    {
        const int oversizedCharacters = 32769;
        var client = new FakeGenerateClient { Response = new string('x', oversizedCharacters) };
        var classifier = NewClassifier(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => classifier.GenerateAsync("Return JSON.",
            TestContext.Current.CancellationToken));

        Assert.False(client.ReadPastAnswer);
    }

    [Fact]
    public async Task JobCancellationStopsStructuredGeneration()
    {
        var client = new FakeGenerateClient();
        using var classifier = NewClassifier(client);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\"}");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => classifier.GenerateAsync("Return JSON.",
            schema.RootElement, cancellation.Token));

        Assert.Equal(0, client.ReadChunks);
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
