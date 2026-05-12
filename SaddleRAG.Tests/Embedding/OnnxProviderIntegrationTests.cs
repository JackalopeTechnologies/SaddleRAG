// OnnxProviderIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     End-to-end checks that the ONNX providers actually load model files
///     and produce sensible output. Skips when the model files aren't
///     staged on disk so CI without them stays green; runs locally once
///     the developer has run the Phase 1 spike or otherwise populated
///     Scratch/onnx-spike/models/{nomic,mxbai}/.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OnnxProviderIntegrationTests
{
    [Fact]
    public async Task EmbeddingProviderProducesL2NormalizedVectorOfExpectedDimension()
    {
        Assert.SkipUnless(File.Exists(NomicModelPath) && File.Exists(NomicVocabPath),
                          $"Nomic ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithNomic();
        var options = Options.Create(settings);

        using var provider = new OnnxEmbeddingProvider(options, NullLogger<OnnxEmbeddingProvider>.Instance);

        Assert.Equal("onnx", provider.ProviderId);
        Assert.Equal("nomic-embed-text-v1.5", provider.ModelName);
        Assert.Equal(NomicDimensions, provider.Dimensions);

        var vectors = await provider.EmbedAsync(new[] { "hello world" }, CancellationToken.None);

        Assert.Single(vectors);
        Assert.Equal(NomicDimensions, vectors[0].Length);

        double norm = 0.0;
        foreach (var v in vectors[0])
            norm += (double) v * v;
        norm = Math.Sqrt(norm);
        Assert.InRange(norm, 0.99, 1.01);
    }

    [Fact]
    public async Task EmbeddingProviderHandlesEmptyInputList()
    {
        Assert.SkipUnless(File.Exists(NomicModelPath) && File.Exists(NomicVocabPath),
                          $"Nomic ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithNomic();
        var options = Options.Create(settings);

        using var provider = new OnnxEmbeddingProvider(options, NullLogger<OnnxEmbeddingProvider>.Instance);

        var vectors = await provider.EmbedAsync(Array.Empty<string>(), CancellationToken.None);

        Assert.Empty(vectors);
    }

    [Fact]
    public async Task ReRankerRanksParisAboveBerlinForCapitalOfFranceQuery()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithMxbai();
        var options = Options.Create(settings);

        using var reranker = new OnnxReRanker(options, NullLogger<OnnxReRanker>.Instance);

        Assert.Equal("mxbai-rerank-base-v1", reranker.ModelName);

        var candidates = new List<DocChunk>
                             {
                                 BuildChunk("paris", "Paris is the capital of France."),
                                 BuildChunk("berlin", "Berlin is the capital of Germany."),
                                 BuildChunk("seine", "The Seine river runs through Paris.")
                             };

        var ranked = await reranker.ReRankAsync("What is the capital of France?",
                                                candidates,
                                                candidates.Count,
                                                CancellationToken.None
                                               );

        Assert.Equal(3, ranked.Count);
        Assert.Equal("paris", ranked[0].Chunk.Id);
        Assert.Equal("berlin", ranked[^1].Chunk.Id);
    }

    [Fact]
    public async Task ReRankerDisabledRegistryActsAsPassThrough()
    {
        // No model file access needed — registry is empty, ActiveRerankerModel
        // resolves to null and reranker pass-through ignores model files.
        var settings = new OnnxSettings();
        var options = Options.Create(settings);

        using var reranker = new OnnxReRanker(options, NullLogger<OnnxReRanker>.Instance);

        Assert.Equal(string.Empty, reranker.ModelName);

        var candidates = new List<DocChunk>
                             {
                                 BuildChunk("a", "first"),
                                 BuildChunk("b", "second"),
                                 BuildChunk("c", "third")
                             };

        var ranked = await reranker.ReRankAsync("any query", candidates, 2, CancellationToken.None);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("a", ranked[0].Chunk.Id);
        Assert.Equal("b", ranked[1].Chunk.Id);
        Assert.True(ranked[0].RelevanceScore > ranked[1].RelevanceScore);
    }

    private static DocChunk BuildChunk(string id, string content) => new()
                                                                         {
                                                                             Id = id,
                                                                             LibraryId = "lib",
                                                                             Version = "v",
                                                                             PageUrl = "https://x",
                                                                             PageTitle = "t",
                                                                             Category = DocCategory.Sample,
                                                                             Content = content
                                                                         };

    private static OnnxSettings BuildSettingsWithNomic()
    {
        var settings = new OnnxSettings
                           {
                               Enabled = true,
                               EmbeddingEnabled = true,
                               ActiveEmbeddingModel = "nomic-embed-text-v1.5",
                               ModelsDir = ScratchModelsRoot,
                               GraphOptimizationLevel = "Basic"
                           };
        settings.EmbeddingModels.Add(new EmbeddingModelEntry
                                         {
                                             Name = "nomic-embed-text-v1.5",
                                             RepoId = "nomic-ai/nomic-embed-text-v1.5",
                                             ModelFile = "onnx/model_fp16.onnx",
                                             TokenizerFamily = TokenizerFamily.Bert,
                                             VocabFile = "vocab.txt",
                                             Dimensions = NomicDimensions,
                                             MaxSequenceLength = 512,
                                             DocumentPrefix = "search_document: ",
                                             QueryPrefix = "search_query: "
                                         });
        return settings;
    }

    private static OnnxSettings BuildSettingsWithMxbai()
    {
        var settings = new OnnxSettings
                           {
                               Enabled = true,
                               EmbeddingEnabled = false,
                               ActiveRerankerModel = "mxbai-rerank-base-v1",
                               ModelsDir = ScratchModelsRoot,
                               GraphOptimizationLevel = "Basic",
                               RerankBatchSize = 16
                           };
        var entry = new RerankerModelEntry
                        {
                            Name = "mxbai-rerank-base-v1",
                            RepoId = "mixedbread-ai/mxbai-rerank-base-v1",
                            ModelFile = "onnx/model_quantized.onnx",
                            TokenizerFamily = TokenizerFamily.SentencePiece,
                            SpmFile = "spm.model",
                            MaxSequenceLength = 512
                        };
        entry.SpecialTokens["[CLS]"] = 1;
        entry.SpecialTokens["[SEP]"] = 2;
        entry.SpecialTokens["[PAD]"] = 0;
        entry.SpecialTokens["[UNK]"] = 3;
        entry.SpecialTokens["[MASK]"] = 128000;
        settings.RerankerModels.Add(entry);
        return settings;
    }

    private static string LocateScratchRoot()
    {
        // Tests run from SaddleRAG.Tests/bin/{Debug,Release}/net10.0/.
        // Walk up to the repo root (which contains SaddleRAG.slnx) and
        // resolve Scratch/onnx-spike/models.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, RepoMarker)))
            current = current.Parent;
        string root = current?.FullName ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, "Scratch", "onnx-spike", "models");
    }

    private static readonly string ScratchModelsRoot = LocateScratchRoot();

    private static string NomicModelPath => Path.Combine(ScratchModelsRoot, "nomic-embed-text-v1.5", "model.onnx");
    private static string NomicVocabPath => Path.Combine(ScratchModelsRoot, "nomic-embed-text-v1.5", "vocab.txt");
    private static string MxbaiModelPath => Path.Combine(ScratchModelsRoot, "mxbai-rerank-base-v1", "model.onnx");
    private static string MxbaiSpmPath => Path.Combine(ScratchModelsRoot, "mxbai-rerank-base-v1", "spm.model");

    private const int NomicDimensions = 768;
    private const string RepoMarker = "SaddleRAG.slnx";
}
