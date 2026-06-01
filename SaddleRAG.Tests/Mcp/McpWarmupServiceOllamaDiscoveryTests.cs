// McpWarmupServiceOllamaDiscoveryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Locks in the contract that <c>McpWarmupService</c> only passes
///     Ollama-embedded model names to the OllamaBootstrapper. The bug
///     this guards against: a library reembedded against ONNX has its
///     <c>EmbeddingModelName</c> updated to a Hugging Face name (e.g.
///     <c>nomic-embed-text-v1.5</c>) that Ollama's manifest endpoint
///     returns 404 on. Without filtering, that throws inside
///     <c>OllamaBootstrapper.BootstrapAsync</c>, the outer warmup catch
///     fires, and chunk loading never runs — leaving the in-memory
///     vector index empty and bricking search after every restart.
/// </summary>
public sealed class McpWarmupServiceOllamaDiscoveryTests
{
    [Fact]
    public void ResolveOllamaModelNameReturnsNullForNullVersion()
    {
        string? result = McpWarmupService.ResolveOllamaModelName(version: null);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveOllamaModelNameReturnsNullWhenModelNameIsEmpty()
    {
        var version = BuildVersion(OllamaEmbeddingProvider.ProviderIdName, string.Empty);

        string? result = McpWarmupService.ResolveOllamaModelName(version);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveOllamaModelNameReturnsNullForOnnxProvider()
    {
        var version = BuildVersion(OnnxProviderId, OnnxModelName);

        string? result = McpWarmupService.ResolveOllamaModelName(version);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveOllamaModelNameReturnsModelNameForOllamaProvider()
    {
        var version = BuildVersion(OllamaEmbeddingProvider.ProviderIdName, OllamaModelName);

        string? result = McpWarmupService.ResolveOllamaModelName(version);

        Assert.Equal(OllamaModelName, result);
    }

    [Fact]
    public void ResolveOllamaModelNameMatchesProviderIdCaseInsensitively()
    {
        var version = BuildVersion(OllamaProviderIdMixedCase, OllamaModelName);

        string? result = McpWarmupService.ResolveOllamaModelName(version);

        Assert.Equal(OllamaModelName, result);
    }

    [Fact]
    public void ResolveOllamaModelNameReturnsNullForUnknownProviderId()
    {
        var version = BuildVersion(UnknownProviderId, OllamaModelName);

        string? result = McpWarmupService.ResolveOllamaModelName(version);

        Assert.Null(result);
    }

    private static LibraryVersionRecord BuildVersion(string providerId, string modelName)
    {
        return new LibraryVersionRecord
                   {
                       Id = $"{LibraryId}-{VersionName}",
                       LibraryId = LibraryId,
                       Version = VersionName,
                       ScrapedAt = new DateTime(year: 2026, month: 5, day: 13, hour: 0, minute: 0, second: 0, DateTimeKind.Utc),
                       PageCount = TestPageCount,
                       ChunkCount = TestChunkCount,
                       EmbeddingProviderId = providerId,
                       EmbeddingModelName = modelName,
                       EmbeddingDimensions = EmbeddingDimensions
                   };
    }

    private const string OnnxProviderId = "onnx";
    private const string OnnxModelName = "nomic-embed-text-v1.5";
    private const string OllamaModelName = "nomic-embed-text";
    private const string OllamaProviderIdMixedCase = "Ollama";
    private const string UnknownProviderId = "openai";
    private const string LibraryId = "test-lib";
    private const string VersionName = "current";
    private const int EmbeddingDimensions = 768;
    private const int TestPageCount = 10;
    private const int TestChunkCount = 50;
}
