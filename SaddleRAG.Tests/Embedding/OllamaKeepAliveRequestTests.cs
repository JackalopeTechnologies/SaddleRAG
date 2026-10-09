// OllamaKeepAliveRequestTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OllamaSharp;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Recon;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     SaddleRAG keeps its own Ollama models loaded by asking on each request,
///     instead of the installer setting OLLAMA_KEEP_ALIVE machine-wide (which pinned
///     every other program's models in GPU memory too). Ollama rejects the bare
///     string "-1" ("missing unit in duration"); "-1m" means "keep loaded".
/// </summary>
public sealed class OllamaKeepAliveRequestTests
{
    [Fact]
    public async Task EmbeddingRequestsAskOllamaToKeepTheEmbeddingModelLoaded()
    {
        var handler = new RecordingOllamaHandler("{\"model\":\"nomic-embed-text\",\"embeddings\":[[0.1,0.2,0.3]]}");
        var provider = new OllamaEmbeddingProvider(Options.Create(new OllamaSettings()),
                                                   NullLogger<OllamaEmbeddingProvider>.Instance,
                                                   MakeClient(handler)
                                                  );

        await provider.EmbedAsync(["hello"], ct: TestContext.Current.CancellationToken);

        Assert.Equal("/api/embed", handler.LastPath);
        Assert.Contains(ExpectedKeepAliveJson, handler.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconRequestsAskOllamaToKeepTheReconModelLoaded()
    {
        var handler = new RecordingOllamaHandler("{\"response\":\"not json\",\"done\":true}\n");
        var settings = new OllamaSettings { ReconModels = { new OllamaModelEntry { Name = "test-recon:latest" } } };
        var recon = new CliReconFallback(Options.Create(settings),
                                         NullLogger<CliReconFallback>.Instance,
                                         MakeClient(handler)
                                        );

        await recon.ReconAsync("https://example.com/docs", "lib", "1.0", TestContext.Current.CancellationToken);

        Assert.Equal("/api/generate", handler.LastPath);
        Assert.Contains(ExpectedKeepAliveJson, handler.LastBody, StringComparison.Ordinal);
    }

    private static OllamaApiClient MakeClient(RecordingOllamaHandler handler) =>
        new OllamaApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434") });

    private const string ExpectedKeepAliveJson = "\"keep_alive\":\"-1m\"";
}
