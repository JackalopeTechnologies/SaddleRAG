// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Symbols;

namespace SaddleRAG.Tests.Ingestion;

public sealed class IngestionPageProcessorTests
{
    [Fact]
    public async Task SharedEmbeddingBoundaryUsesBoundedProviderBatches()
    {
        var provider = new RecordingEmbeddingProvider();
        var processor = new IngestionPageProcessor(Substitute.For<ILlmClassifier>(),
                                                   new CategoryAwareChunker(new SymbolExtractor()),
                                                   provider,
                                                   new InMemoryBruteForceVectorSearch(),
                                                   NullLogger<IngestionPageProcessor>.Instance);
        IReadOnlyList<DocChunk> chunks = Enumerable.Range(0, ChunkCount)
                                                   .Select(CreateChunk)
                                                   .ToList();

        DocChunk[] result = await processor.EmbedAsync(chunks, TestContext.Current.CancellationToken);

        Assert.Equal(ChunkCount, result.Length);
        Assert.Equal([FirstBatchSize, SecondBatchSize, FinalBatchSize], provider.BatchSizes);
        Assert.All(result, chunk => Assert.NotNull(chunk.Embedding));
    }

    private static DocChunk CreateChunk(int index) => new()
        {
            Id = $"chunk-{index}",
            LibraryId = "manual-library",
            Version = "2026-08-04",
            PageUrl = $"saddlerag://page/{index}",
            PageTitle = "Manual",
            Category = DocCategory.HowTo,
            Content = $"Content {index}"
        };

    private sealed class RecordingEmbeddingProvider : IEmbeddingProvider
    {
        public List<int> BatchSizes { get; } = [];

        public string ProviderId => "recording";

        public string ModelName => "recording-v1";

        public int Dimensions => 2;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts,
                                          EmbedRole role = EmbedRole.Document,
                                          CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            BatchSizes.Add(texts.Count);
            return Task.FromResult(texts.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }

    private const int ChunkCount = 65;
    private const int FirstBatchSize = 32;
    private const int SecondBatchSize = 32;
    private const int FinalBatchSize = 1;
}
