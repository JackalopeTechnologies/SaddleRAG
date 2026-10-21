// OllamaEmbeddingProviderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OllamaSharp;
using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OllamaEmbeddingProviderTests
{
    [Theory]
    [InlineData("nomic-embed-text", EmbedRole.Document, "search_document: ")]
    [InlineData("nomic-embed-text:latest", EmbedRole.Query, "search_query: ")]
    [InlineData("embeddinggemma-2:270m", EmbedRole.Document, "title: none | text: ")]
    [InlineData("embeddinggemma-2:270m", EmbedRole.Query, "task: search result | query: ")]
    public async Task SendsModelSpecificTaskInstructions(string model, EmbedRole role, string prefix)
    {
        using var handler = new EmbeddingResponseHandler();
        var settings = Settings();
        settings.EmbeddingModel = model;
        var provider = Provider(settings, handler);

        await provider.EmbedAsync(["test passage"], role, TestContext.Current.CancellationToken);

        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal(prefix + "test passage", request.RootElement.GetProperty("input")[0].GetString());
        Assert.False(request.RootElement.GetProperty("truncate").GetBoolean());
        Assert.Equal(4, request.RootElement.GetProperty("options").GetProperty("num_thread").GetInt32());
    }

    [Fact]
    public async Task PreservesLegacyVectorsUntilTaskInstructionsAreExplicitlyEnabled()
    {
        using var handler = new EmbeddingResponseHandler();
        var settings = new OllamaSettings { EmbeddingDimensions = 2 };

        await Provider(settings, handler).EmbedAsync(["unchanged"], EmbedRole.Query, TestContext.Current.CancellationToken);

        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal("unchanged", request.RootElement.GetProperty("input")[0].GetString());
    }

    [Fact]
    public async Task SendsBoundedBatchesAndPreservesInputOrder()
    {
        using var handler = new EmbeddingResponseHandler();
        var settings = Settings();
        settings.EmbeddingBatchSize = 2;
        var provider = Provider(settings, handler);

        float[][] result = await provider.EmbedAsync(["a", "b", "c", "d", "e"], ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f }, result.Select(vector => vector[0]));
        Assert.Equal(new[] { 2, 2, 1 }, handler.Requests.Select(body =>
        {
            using var request = JsonDocument.Parse(body);
            return request.RootElement.GetProperty("input").GetArrayLength();
        }));
    }

    [Fact]
    public async Task EmptyInputDoesNotLoadModel()
    {
        using var handler = new EmbeddingResponseHandler();
        var result = await Provider(Settings(), handler).EmbedAsync([], ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[[1]]")]
    [InlineData("[[0,0]]")]
    [InlineData("[[1e39,1]]")]
    [InlineData("[[1,1],[2,2]]")]
    public async Task RejectsMalformedVectorsWithoutRetry(string vectors)
    {
        using var handler = new EmbeddingResponseHandler
        {
            Response = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"embeddings\":" + vectors + "}")
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Provider(Settings(), handler).EmbedAsync(["a"], ct: TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RetriesTransportFailureAndThenReturnsVectors()
    {
        using var handler = new EmbeddingResponseHandler();
        handler.Response = () => handler.Requests.Count == 1
            ? throw new HttpRequestException("connection reset")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"embeddings\":[[1,2]]}") };

        var result = await Provider(Settings(), handler).EmbedAsync(["a"], ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(new[] { 1f, 2f }, Assert.Single(result));
    }

    [Fact]
    public async Task CancellationStopsRetriesImmediately()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new EmbeddingResponseHandler();
        handler.Response = () =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider(Settings(), handler).EmbedAsync(["a", "b"], ct: cancellation.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RejectsPreCancelledCallBeforeSendingRequest()
    {
        using var handler = new EmbeddingResponseHandler();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider(Settings(), handler).EmbedAsync(["a"], ct: new CancellationToken(canceled: true)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DoesNotRetryPermanentRequestFailure()
    {
        using var handler = new EmbeddingResponseHandler
        {
            Response = () => throw new HttpRequestException("invalid input", null, HttpStatusCode.BadRequest)
        };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Provider(Settings(), handler).EmbedAsync(["a"], ct: TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RejectsUnknownTaskFormatBeforeSendingRequest()
    {
        using var handler = new EmbeddingResponseHandler();
        var settings = Settings();
        settings.EmbeddingModel = "unconfigured-model";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(settings, handler).EmbedAsync(["a"], ct: TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(65, 60)]
    [InlineData(1, -1)]
    [InlineData(1, 301)]
    public void RejectsUnboundedBatchOrRetention(int batchSize, int keepAlive)
    {
        using var handler = new EmbeddingResponseHandler();
        var settings = Settings();
        settings.EmbeddingBatchSize = batchSize;
        settings.EmbeddingKeepAliveSeconds = keepAlive;
        Assert.Throws<ArgumentOutOfRangeException>(() => Provider(settings, handler));
    }

    [Fact]
    public async Task SupportsImmediateUnload()
    {
        using var handler = new EmbeddingResponseHandler();
        var settings = Settings();
        settings.EmbeddingKeepAliveSeconds = 0;
        await Provider(settings, handler).EmbedAsync(["a"], ct: TestContext.Current.CancellationToken);
        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal("0s", request.RootElement.GetProperty("keep_alive").GetString());
    }

    private static OllamaSettings Settings() => new() { EmbeddingDimensions = 2, EmbeddingUseTaskPrefixes = true };

    [Theory]
    [InlineData("embeddinggemma-2:270m", null, 8192)]
    [InlineData("embeddinggemma-2:270m", 4096, 4096)]
    [InlineData("nomic-embed-text", 2048, 2048)]
    public async Task FitsCompleteInputInRunnerTokenBatch(string model, int? configured, int expected)
    {
        using var handler = new EmbeddingResponseHandler();
        var settings = Settings();
        settings.EmbeddingModel = model;
        settings.EmbeddingContextLength = configured;

        await Provider(settings, handler).EmbedAsync([new string('x', 2400)], ct: TestContext.Current.CancellationToken);

        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        var options = request.RootElement.GetProperty("options");
        Assert.Equal(expected, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(expected, options.GetProperty("num_batch").GetInt32());
        Assert.False(request.RootElement.GetProperty("truncate").GetBoolean());
    }

    [Fact]
    public async Task PreservesOtherModelContextDefaults()
    {
        using var handler = new EmbeddingResponseHandler();
        await Provider(Settings(), handler).EmbedAsync(["passage"], ct: TestContext.Current.CancellationToken);
        using var request = JsonDocument.Parse(Assert.Single(handler.Requests));
        var options = request.RootElement.GetProperty("options");
        Assert.False(options.TryGetProperty("num_ctx", out _));
        Assert.False(options.TryGetProperty("num_batch", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsInvalidContextBeforeSendingRequest(int contextLength)
    {
        using var handler = new EmbeddingResponseHandler();
        var settings = Settings();
        settings.EmbeddingContextLength = contextLength;
        Assert.Throws<ArgumentOutOfRangeException>(() => Provider(settings, handler));
        Assert.Empty(handler.Requests);
    }

    private static OllamaEmbeddingProvider Provider(OllamaSettings settings, EmbeddingResponseHandler handler) =>
        new(Options.Create(settings), NullLogger<OllamaEmbeddingProvider>.Instance,
            new OllamaApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") }));
}
