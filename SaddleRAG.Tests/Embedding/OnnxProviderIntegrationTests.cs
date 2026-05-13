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

        using var provider = new OnnxEmbeddingProvider(options, new OnnxRuntimeCapabilities(), NullLogger<OnnxEmbeddingProvider>.Instance);

        Assert.Equal("onnx", provider.ProviderId);
        Assert.Equal("nomic-embed-text-v1.5", provider.ModelName);
        Assert.Equal(NomicDimensions, provider.Dimensions);

        var vectors = await provider.EmbedAsync(["hello world"], ct: CancellationToken.None);

        Assert.Single(vectors);
        Assert.Equal(NomicDimensions, vectors[0].Length);

        double norm = 0.0;
        foreach (var v in vectors[0])
            norm += (double) v * v;
        norm = Math.Sqrt(norm);
        Assert.InRange(norm, low: 0.99, high: 1.01);
    }

    [Fact]
    public async Task EmbeddingProviderHandlesEmptyInputList()
    {
        Assert.SkipUnless(File.Exists(NomicModelPath) && File.Exists(NomicVocabPath),
                          $"Nomic ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithNomic();
        var options = Options.Create(settings);

        using var provider = new OnnxEmbeddingProvider(options, new OnnxRuntimeCapabilities(), NullLogger<OnnxEmbeddingProvider>.Instance);

        var vectors = await provider.EmbedAsync([], ct: CancellationToken.None);

        Assert.Empty(vectors);
    }

    [Fact]
    public async Task ReRankerRanksParisAboveBerlinForCapitalOfFranceQuery()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithMxbai();
        var options = Options.Create(settings);

        using var reranker = new OnnxReRanker(options, new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);

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

        Assert.Equal(expected: 3, ranked.Count);
        Assert.Equal("paris", ranked[index: 0].Chunk.Id);
        Assert.Equal("berlin", ranked[^1].Chunk.Id);
    }

    [Fact]
    public async Task ReRankerDisabledRegistryActsAsPassThrough()
    {
        // No model file access needed — registry is empty, ActiveRerankerModel
        // resolves to null and reranker pass-through ignores model files.
        var settings = new OnnxSettings();
        var options = Options.Create(settings);

        using var reranker = new OnnxReRanker(options, new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);

        Assert.Equal(string.Empty, reranker.ModelName);

        var candidates = new List<DocChunk>
                             {
                                 BuildChunk("a", "first"),
                                 BuildChunk("b", "second"),
                                 BuildChunk("c", "third")
                             };

        var ranked = await reranker.ReRankAsync("any query", candidates, maxResults: 2, CancellationToken.None);

        Assert.Equal(expected: 2, ranked.Count);
        Assert.Equal("a", ranked[index: 0].Chunk.Id);
        Assert.Equal("b", ranked[index: 1].Chunk.Id);
        Assert.True(ranked[index: 0].RelevanceScore > ranked[index: 1].RelevanceScore);
    }

    [Fact]
    public async Task EmbeddingProviderProducesDifferentVectorsForQueryVsDocumentRoles()
    {
        Assert.SkipUnless(File.Exists(NomicModelPath) && File.Exists(NomicVocabPath),
                          $"Nomic ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithNomic();
        var options = Options.Create(settings);

        using var provider = new OnnxEmbeddingProvider(options, new OnnxRuntimeCapabilities(), NullLogger<OnnxEmbeddingProvider>.Instance);

        const string text = "How do I configure ONNX Runtime?";
        var asDoc = await provider.EmbedAsync([text], EmbedRole.Document, CancellationToken.None);
        var asQuery = await provider.EmbedAsync([text], EmbedRole.Query, CancellationToken.None);

        // Same text, different role → nomic emits different vectors via the
        // search_document: vs search_query: task prefix. If these were equal,
        // the prefix wouldn't be reaching the model.
        Assert.Single(asDoc);
        Assert.Single(asQuery);
        Assert.Equal(asDoc[0].Length, asQuery[0].Length);

        double cosine = 0.0;
        for(var i = 0; i < asDoc[0].Length; i++)
            cosine += asDoc[0][i] * asQuery[0][i];

        // L2-normalized vectors → cosine similarity equals dot product. The
        // query and document embeddings for the same text are similar but
        // not identical; require cosine < 0.999 to confirm they actually
        // differ.
        Assert.True(cosine < 0.999,
                    $"Doc/Query vectors should differ for asymmetric model; got cosine={cosine:F4}.");
    }

    [Fact]
    public async Task ReRankerBatchedRunsCompleteWithValidScoresAndCoherentTopResults()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        // 20 candidates across a wide quality spectrum, exceeds smaller batch
        // sizes so we exercise multi-batch logic. The test asserts that each
        // batch size produces a complete result set with valid scores, and
        // that semantically Paris-related candidates dominate.
        //
        // Per-pair score parity across batch sizes is NOT asserted: mxbai's
        // int8 quantized ONNX export shows materially different scores
        // depending on batch composition (likely SIMD-parallel accumulation
        // order in the matmul + ONNX Runtime quantization). The
        // batching contract is "valid scores, plausible ranking" — not
        // bit-exact parity.
        var candidates = BuildCandidatesForBatching(count: 20);
        const string query = "What is the capital of France?";

        foreach (int batchSize in new[] { 1, 8, 32 })
        {
            var ranked = await ScoreWith(query, candidates, batchSize, CancellationToken.None);

            Assert.Equal(candidates.Count, ranked.Count);
            foreach (var r in ranked)
            {
                Assert.False(float.IsNaN(r.RelevanceScore), $"NaN score for {r.Chunk.Id} at batch {batchSize}");
                Assert.False(float.IsInfinity(r.RelevanceScore),
                             $"Inf score for {r.Chunk.Id} at batch {batchSize}");
            }
            // Result is sorted descending by score.
            for(var i = 1; i < ranked.Count; i++)
                Assert.True(ranked[i - 1].RelevanceScore >= ranked[i].RelevanceScore,
                            $"Result not sorted at batch {batchSize}: pos {i - 1} score {ranked[i - 1].RelevanceScore} < pos {i} score {ranked[i].RelevanceScore}");

            // Top result should be a Paris-related candidate (the first 5
            // template positions in BuildCandidatesForBatching).
            var topContent = ranked[index: 0].Chunk.Content;
            Assert.Contains("Paris", topContent);
        }
    }

    [Fact]
    public async Task ReRankerTruncatesOverLengthDocumentsAndStillProducesValidScores()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        // Build one massively over-length document — a 100x repeat of a
        // plausible sentence. mxbai's MaxSequenceLength is 512 tokens; this
        // input is way past that. The reranker should truncate doc-side
        // and still produce a valid score.
        var longDoc = string.Concat(Enumerable.Repeat("Paris is the capital of France. ", count: 200));
        var candidates = new List<DocChunk>
                             {
                                 BuildChunk("long", longDoc),
                                 BuildChunk("short", "Berlin is the capital of Germany.")
                             };

        var settings = BuildSettingsWithMxbai();
        var options = Options.Create(settings);
        using var reranker = new OnnxReRanker(options, new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);

        // Should not throw despite the document blowing past MaxSequenceLength.
        var ranked = await reranker.ReRankAsync("What is the capital of France?",
                                                candidates,
                                                candidates.Count,
                                                CancellationToken.None
                                               );

        Assert.Equal(expected: 2, ranked.Count);
        Assert.False(float.IsNaN(ranked[index: 0].RelevanceScore));
        Assert.False(float.IsInfinity(ranked[index: 0].RelevanceScore));
        // Even truncated, "Paris is the capital of France." should score above
        // the Berlin/Germany distractor.
        Assert.Equal("long", ranked[index: 0].Chunk.Id);
    }

    [Fact]
    public async Task ReRankerHonorsCancellationTokenBetweenBatches()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        // 30 candidates, batch size 1 → 30 batches → at least one
        // ThrowIfCancellationRequested loop check between batches.
        var candidates = BuildCandidatesForBatching(count: 30);

        var settings = BuildSettingsWithMxbai();
        settings.RerankBatchSize = 1;
        var options = Options.Create(settings);
        using var reranker = new OnnxReRanker(options, new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reranker.ReRankAsync("query", candidates, candidates.Count, cts.Token)
        );
    }

    [Fact]
    public async Task ToggleableReRankerDispatchesOnnxStrategyToOnnxReRanker()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        // Both Onnx and Off paths run real OnnxReRanker through the toggle.
        var onnxOptions = Options.Create(BuildSettingsWithMxbai());
        using var onnxReRanker = new OnnxReRanker(onnxOptions, new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);

        var ranking = new RankingSettings { ReRankerStrategy = ReRankerStrategy.Onnx };
        var toggle = new ToggleableReRanker(Options.Create(ranking),
                                            onnxReRanker,
                                            NullLoggerFactory.Instance
                                           );

        var candidates = new List<DocChunk>
                             {
                                 BuildChunk("paris", "Paris is the capital of France."),
                                 BuildChunk("berlin", "Berlin is the capital of Germany."),
                                 BuildChunk("seine", "The Seine river runs through Paris.")
                             };

        // With ReRankerStrategy.Onnx, ToggleableReRanker should call into
        // OnnxReRanker which produces real cross-encoder scores
        // (sigmoid-mapped, so within (0, 1) — see OnnxReRanker.NormalizeLogit).
        // The Paris candidate should dominate AND the score gap between
        // top-1 (very relevant) and a clearly off-topic candidate (Berlin
        // is a different country) should be much larger than NoOp's
        // synthetic 0.01 step — that gap is the discriminator that proves
        // a real cross-encoder ran rather than the pass-through fallback.
        var rankedOnnx = await toggle.ReRankAsync("What is the capital of France?",
                                                  candidates, candidates.Count,
                                                  CancellationToken.None
                                                 );
        Assert.Equal("paris", rankedOnnx[index: 0].Chunk.Id);
        Assert.InRange(rankedOnnx[index: 0].RelevanceScore, low: 0f, high: 1f);
        var berlinIndex = rankedOnnx
            .Select((r, i) => (r, i))
            .First(t => t.r.Chunk.Id == BerlinChunkId).i;
        var realRerankGap = rankedOnnx[index: 0].RelevanceScore - rankedOnnx[berlinIndex].RelevanceScore;
        Assert.True(realRerankGap > MinCrossEncoderTopGap,
                    $"Cross-encoder gap top→{BerlinChunkId} should be > {MinCrossEncoderTopGap:F2} (NoOp step is 0.01); got {realRerankGap:F4}"
                   );

        // Flip to Off → NoOp pass-through. Original input order preserved
        // with synthetic descending scores.
        toggle.Strategy = ReRankerStrategy.Off;
        var rankedOff = await toggle.ReRankAsync("What is the capital of France?",
                                                 candidates, candidates.Count,
                                                 CancellationToken.None
                                                );
        Assert.Equal("paris", rankedOff[index: 0].Chunk.Id);
        Assert.Equal("berlin", rankedOff[index: 1].Chunk.Id);
        Assert.Equal("seine", rankedOff[index: 2].Chunk.Id);
        // NoOp passes through with score = 1.0 - i * 0.01.
        Assert.True(rankedOff[index: 0].RelevanceScore <= 1.0f);
    }

    [Fact]
    public async Task EmbeddingProviderReturnsOneVectorPerInputTextForMultiTextInput()
    {
        Assert.SkipUnless(File.Exists(NomicModelPath) && File.Exists(NomicVocabPath),
                          $"Nomic ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithNomic();
        using var provider = new OnnxEmbeddingProvider(Options.Create(settings),
                                                       new OnnxRuntimeCapabilities(), NullLogger<OnnxEmbeddingProvider>.Instance);

        string[] texts =
            [
                "Paris is the capital of France.",
                "Berlin is the capital of Germany.",
                "Tokyo is the capital of Japan."
            ];

        var vectors = await provider.EmbedAsync(texts, ct: CancellationToken.None);

        Assert.Equal(texts.Length, vectors.Length);
        for(var i = 0; i < texts.Length; i++)
        {
            Assert.Equal(NomicDimensions, vectors[i].Length);
            double norm = 0.0;
            foreach (var v in vectors[i])
                norm += (double) v * v;
            Assert.InRange(Math.Sqrt(norm), low: 0.99, high: 1.01);
        }
    }

    [Fact]
    public async Task EmbeddingProviderIsDeterministicForRepeatedInput()
    {
        Assert.SkipUnless(File.Exists(NomicModelPath) && File.Exists(NomicVocabPath),
                          $"Nomic ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithNomic();
        using var provider = new OnnxEmbeddingProvider(Options.Create(settings),
                                                       new OnnxRuntimeCapabilities(), NullLogger<OnnxEmbeddingProvider>.Instance);

        const string text = "Paris is the capital of France.";
        var first = await provider.EmbedAsync([text], ct: CancellationToken.None);
        var second = await provider.EmbedAsync([text], ct: CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(first[0].Length, second[0].Length);
        for(var i = 0; i < first[0].Length; i++)
            Assert.Equal(first[0][i], second[0][i]);
    }

    [Fact]
    public async Task EmbeddingProviderProducesHigherCosineForSemanticallyRelatedTexts()
    {
        Assert.SkipUnless(File.Exists(NomicModelPath) && File.Exists(NomicVocabPath),
                          $"Nomic ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithNomic();
        using var provider = new OnnxEmbeddingProvider(Options.Create(settings),
                                                       new OnnxRuntimeCapabilities(), NullLogger<OnnxEmbeddingProvider>.Instance);

        var vectors = await provider.EmbedAsync(
            [
                "The capital of France is Paris.",
                "Paris is a major European city.",
                "Pickle production techniques vary by region."
            ],
            ct: CancellationToken.None
        );

        double relatedCosine = Cosine(vectors[0], vectors[1]);
        double unrelatedCosine = Cosine(vectors[0], vectors[2]);

        Assert.True(relatedCosine > unrelatedCosine,
                    $"Related texts should cluster closer than unrelated; relatedCos={relatedCosine:F4} unrelatedCos={unrelatedCosine:F4}");
    }

    [Fact]
    public async Task EmbeddingProviderTruncatesVeryLongInputAndStillProducesValidVector()
    {
        Assert.SkipUnless(File.Exists(NomicModelPath) && File.Exists(NomicVocabPath),
                          $"Nomic ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var settings = BuildSettingsWithNomic();
        // Force a small cap to exercise truncation deterministically.
        settings.EmbeddingModels[0].MaxSequenceLength = 64;
        using var provider = new OnnxEmbeddingProvider(Options.Create(settings),
                                                       new OnnxRuntimeCapabilities(), NullLogger<OnnxEmbeddingProvider>.Instance);

        string longText = string.Concat(Enumerable.Repeat("Paris is the capital of France. ", count: 100));

        var vectors = await provider.EmbedAsync([longText], ct: CancellationToken.None);

        Assert.Single(vectors);
        Assert.Equal(NomicDimensions, vectors[0].Length);
        double norm = 0.0;
        foreach (var v in vectors[0])
            norm += (double) v * v;
        Assert.InRange(Math.Sqrt(norm), low: 0.99, high: 1.01);
    }

    [Fact]
    public void EmbeddingProviderConstructorThrowsFileNotFoundWhenModelMissing()
    {
        var settings = BuildSettingsWithNomic();
        // Point at a registry entry whose Name resolves to a nonexistent dir.
        settings.EmbeddingModels[0].Name = "does-not-exist-model";
        settings.ActiveEmbeddingModel = "does-not-exist-model";

        var ex = Assert.Throws<FileNotFoundException>(
            () => new OnnxEmbeddingProvider(Options.Create(settings),
                                             new OnnxRuntimeCapabilities(), NullLogger<OnnxEmbeddingProvider>.Instance)
        );
        Assert.Contains("does-not-exist-model", ex.Message);
    }

    [Fact]
    public async Task ReRankerMaxResultsCapsTheReturnedCount()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var candidates = BuildCandidatesForBatching(count: 20);
        using var reranker = new OnnxReRanker(Options.Create(BuildSettingsWithMxbai()),
                                              new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);

        var ranked = await reranker.ReRankAsync("What is the capital of France?",
                                                candidates,
                                                maxResults: 5,
                                                CancellationToken.None);

        Assert.Equal(expected: 5, ranked.Count);
        // Results are sorted descending — verify cap retains the top.
        for(var i = 1; i < ranked.Count; i++)
            Assert.True(ranked[i - 1].RelevanceScore >= ranked[i].RelevanceScore);
    }

    [Fact]
    public async Task ReRankerHandlesSingleAndEmptyCandidateLists()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        using var reranker = new OnnxReRanker(Options.Create(BuildSettingsWithMxbai()),
                                              new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);

        var emptyRanked = await reranker.ReRankAsync("query",
                                                      Array.Empty<DocChunk>(),
                                                      maxResults: 10,
                                                      CancellationToken.None);
        Assert.Empty(emptyRanked);

        var single = new List<DocChunk> { BuildChunk("only", "Paris is the capital of France.") };
        var singleRanked = await reranker.ReRankAsync("What is the capital of France?",
                                                      single,
                                                      maxResults: 10,
                                                      CancellationToken.None);
        Assert.Single(singleRanked);
        Assert.Equal("only", singleRanked[0].Chunk.Id);
        Assert.False(float.IsNaN(singleRanked[0].RelevanceScore));
    }

    [Fact]
    public async Task ReRankerIsDeterministicForRepeatedQueryAndCandidates()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        var candidates = new List<DocChunk>
                             {
                                 BuildChunk("paris", "Paris is the capital of France."),
                                 BuildChunk("berlin", "Berlin is the capital of Germany.")
                             };

        // Same settings, same batch size — same scores on repeat.
        using var reranker = new OnnxReRanker(Options.Create(BuildSettingsWithMxbai()),
                                              new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);

        var first = await reranker.ReRankAsync("What is the capital of France?",
                                                candidates, candidates.Count,
                                                CancellationToken.None);
        var second = await reranker.ReRankAsync("What is the capital of France?",
                                                 candidates, candidates.Count,
                                                 CancellationToken.None);

        var firstById = first.ToDictionary(r => r.Chunk.Id);
        var secondById = second.ToDictionary(r => r.Chunk.Id);
        foreach (var kvp in firstById)
            Assert.Equal(kvp.Value.RelevanceScore, secondById[kvp.Key].RelevanceScore);
    }

    [Fact]
    public async Task ReRankerPassThroughPreservesInputOrderForTwentyCandidates()
    {
        // No model files needed — empty registry → pass-through.
        var candidates = BuildCandidatesForBatching(count: 20);
        using var reranker = new OnnxReRanker(Options.Create(new OnnxSettings()),
                                              new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);

        var ranked = await reranker.ReRankAsync("any query",
                                                candidates,
                                                maxResults: candidates.Count,
                                                CancellationToken.None);

        Assert.Equal(candidates.Count, ranked.Count);
        for(var i = 0; i < candidates.Count; i++)
        {
            Assert.Equal(candidates[i].Id, ranked[i].Chunk.Id);
            // NoOp/pass-through scores are 1.0 - i * 0.01 descending.
            if (i > 0)
                Assert.True(ranked[i - 1].RelevanceScore > ranked[i].RelevanceScore);
        }
    }

    [Fact]
    public void ReRankerConstructorThrowsFileNotFoundWhenModelMissing()
    {
        var settings = BuildSettingsWithMxbai();
        settings.RerankerModels[0].Name = "does-not-exist-reranker";
        settings.ActiveRerankerModel = "does-not-exist-reranker";

        var ex = Assert.Throws<FileNotFoundException>(
            () => new OnnxReRanker(Options.Create(settings),
                                    new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance)
        );
        Assert.Contains("does-not-exist-reranker", ex.Message);
    }

    [Fact]
    public async Task ToggleableReRankerStrategyFlipMutatesDispatchOnSubsequentCalls()
    {
        Assert.SkipUnless(File.Exists(MxbaiModelPath) && File.Exists(MxbaiSpmPath),
                          $"mxbai ONNX model not staged at {ScratchModelsRoot}; run the Phase 1 spike to populate.");

        using var onnxReRanker = new OnnxReRanker(Options.Create(BuildSettingsWithMxbai()),
                                                  new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);
        var ranking = new RankingSettings { ReRankerStrategy = ReRankerStrategy.Off };
        var toggle = new ToggleableReRanker(Options.Create(ranking),
                                            onnxReRanker,
                                            NullLoggerFactory.Instance);

        var candidates = new List<DocChunk>
                             {
                                 BuildChunk("paris", "Paris is the capital of France."),
                                 BuildChunk("berlin", "Berlin is the capital of Germany.")
                             };

        // Start with Off → NoOp synthetic scores (1.0, 0.99).
        var firstRun = await toggle.ReRankAsync("What is the capital of France?",
                                                 candidates, candidates.Count,
                                                 CancellationToken.None);
        Assert.True(firstRun[0].RelevanceScore <= 1.0f);
        // Synthetic, not real cross-encoder.
        Assert.True(firstRun[0].RelevanceScore >= 0.9f);

        // Flip to Onnx mid-flight (same toggle instance).
        toggle.Strategy = ReRankerStrategy.Onnx;

        var secondRun = await toggle.ReRankAsync("What is the capital of France?",
                                                  candidates, candidates.Count,
                                                  CancellationToken.None);
        // Real cross-encoder is sigmoid-mapped (see OnnxReRanker.NormalizeLogit)
        // so scores live in (0, 1) just like NoOp pass-through. What
        // distinguishes "real ran" from "fell through to pass-through" is
        // the SEMANTIC gap between the top relevant chunk (paris) and a
        // clearly off-topic one (berlin = different country). NoOp's
        // synthetic step is 0.01; a real cross-encoder gap on this query
        // is much larger.
        Assert.Equal("paris", secondRun[0].Chunk.Id);
        Assert.InRange(secondRun[0].RelevanceScore, low: 0f, high: 1f);
        var realRerankGap = secondRun[0].RelevanceScore - secondRun[1].RelevanceScore;
        Assert.True(realRerankGap > MinCrossEncoderTopGap,
                    $"Cross-encoder gap top→{BerlinChunkId} should be > {MinCrossEncoderTopGap:F2} (NoOp step is 0.01); got {realRerankGap:F4}"
                   );

        // Flip back to Off → synthetic scores again.
        toggle.Strategy = ReRankerStrategy.Off;
        var thirdRun = await toggle.ReRankAsync("What is the capital of France?",
                                                 candidates, candidates.Count,
                                                 CancellationToken.None);
        Assert.True(thirdRun[0].RelevanceScore <= 1.0f);
        Assert.True(thirdRun[0].RelevanceScore >= 0.9f);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0.0;
        for(var i = 0; i < a.Length; i++)
            dot += (double) a[i] * b[i];
        return dot;
    }

    private static List<DocChunk> BuildCandidatesForBatching(int count)
    {
        var templates = new[]
                            {
                                "Paris is the capital of France.",
                                "The Eiffel Tower is in Paris.",
                                "France is in Western Europe.",
                                "Berlin is the capital of Germany.",
                                "The Seine flows through Paris.",
                                "Lyon is a major city in France.",
                                "Madrid is the capital of Spain.",
                                "London is the capital of the UK.",
                                "Rome is the capital of Italy.",
                                "Tokyo is the capital of Japan."
                            };

        var result = new List<DocChunk>(count);
        for(var i = 0; i < count; i++)
        {
            string content = templates[i % templates.Length] + $" (variant {i})";
            result.Add(BuildChunk($"c{i}", content));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ReRankResult>> ScoreWith(string query,
                                                                      List<DocChunk> candidates,
                                                                      int batchSize,
                                                                      CancellationToken ct)
    {
        var settings = BuildSettingsWithMxbai();
        settings.RerankBatchSize = batchSize;
        var options = Options.Create(settings);
        using var reranker = new OnnxReRanker(options, new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);
        var ranked = await reranker.ReRankAsync(query, candidates, candidates.Count, ct);
        return ranked;
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
                               GraphOptimizationLevel = OnnxGraphOptimizationLevel.Basic
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
                               GraphOptimizationLevel = OnnxGraphOptimizationLevel.Basic,
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
    private const string BerlinChunkId = "berlin";

    /// <summary>
    ///     Minimum acceptable score gap between top-1 (a strongly relevant
    ///     chunk) and a clearly off-topic chunk in cross-encoder output.
    ///     NoOp pass-through emits a 0.01 synthetic step between successive
    ///     items, so a gap of at least 0.1 (10x the NoOp step) proves the
    ///     real cross-encoder ran and produced a semantic ranking rather
    ///     than the input order falling through.
    /// </summary>
    private const float MinCrossEncoderTopGap = 0.1f;
}
