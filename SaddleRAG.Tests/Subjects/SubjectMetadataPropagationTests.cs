// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Chunking;
using SaddleRAG.Ingestion.Symbols;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectMetadataPropagationTests
{
    [Fact]
    public void ChunkingPreservesCategoryBehaviorAndCopiesSubjectMetadata()
    {
        var page = new PageRecord
                       {
                           Id = "page-1",
                           LibraryId = "manual-library",
                           Version = "2026-08-04",
                           Url = "saddlerag://manual-library/documents/document-hydraulics",
                           Title = "Hydraulic manual",
                           Category = DocCategory.HowTo,
                           RawContent = "# Safety\nLock out the pump.\n# Service\nInspect seals.",
                           FetchedAt = SubjectTestData.GeneratedAtUtc,
                           ContentHash = "hash",
                           SubjectIds = ["subject-hydraulics", "subject-safety"],
                           SubjectTaxonomyVersion = "taxonomy-000001",
                           DocumentSource = new DocumentProvenance
                               {
                                   DocumentId = "document-hydraulics",
                                   RevisionId = "revision-hydraulics",
                                   SourceUri = "saddlerag://manual-library/documents/document-hydraulics",
                                   RelativePath = "maintenance/hydraulics-safety.pdf"
                               }
                       };

        IReadOnlyList<DocChunk> chunks = new CategoryAwareChunker(new SymbolExtractor()).Chunk(page);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks,
                   chunk =>
                   {
                       Assert.Equal(DocCategory.HowTo, chunk.Category);
                       Assert.Equal(page.SubjectIds, chunk.SubjectIds);
                       Assert.Equal(page.SubjectTaxonomyVersion, chunk.SubjectTaxonomyVersion);
                       Assert.Same(page.DocumentSource, chunk.DocumentSource);
                   });
    }

    [Fact]
    public async Task EmbeddingTextNeverContainsTaxonomyLabels()
    {
        var provider = new CapturingEmbeddingProvider();
        DocChunk chunk = SubjectTestData.Chunk("chunk-1", "revision-hydraulics") with
                             {
                                 Content = "Neutral service text.",
                                 PageTitle = "Maintenance manual"
                             };

        DocChunk[] embedded = await EmbedStage.EmbedBatchAsync(provider,
                                                               NullLogger.Instance,
                                                               [chunk],
                                                               TestContext.Current.CancellationToken);

        string input = Assert.Single(provider.Texts);
        Assert.DoesNotContain("Hydraulics", input, StringComparison.Ordinal);
        Assert.DoesNotContain("subject-hydraulics", input, StringComparison.Ordinal);
        Assert.Equal(chunk.SubjectIds, Assert.Single(embedded).SubjectIds);
        Assert.Equal(chunk.SubjectTaxonomyVersion, Assert.Single(embedded).SubjectTaxonomyVersion);
    }

    [Fact]
    public void LegacyBsonWithoutSubjectFieldsUsesBackwardCompatibleDefaults()
    {
        var document = new BsonDocument
                           {
                               ["_id"] = "chunk-legacy",
                               ["LibraryId"] = "manual-library",
                               ["Version"] = "legacy",
                               ["PageUrl"] = "https://example.invalid",
                               ["PageTitle"] = "Legacy",
                               ["Category"] = (int)DocCategory.HowTo,
                               ["Content"] = "Legacy content",
                               ["TokenCount"] = 2
                           };

        DocChunk chunk = BsonSerializer.Deserialize<DocChunk>(document);

        Assert.Empty(chunk.SubjectIds);
        Assert.Null(chunk.SubjectTaxonomyVersion);
    }

    private sealed class CapturingEmbeddingProvider : IEmbeddingProvider
    {
        public List<string> Texts { get; } = [];

        public string ProviderId => "capture";

        public string ModelName => "capture-model";

        public int Dimensions => 2;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts,
                                          EmbedRole role = EmbedRole.Document,
                                          CancellationToken ct = default)
        {
            Texts.AddRange(texts);
            return Task.FromResult(texts.Select(_ => new[] { 1.0f, 0.0f }).ToArray());
        }
    }
}
