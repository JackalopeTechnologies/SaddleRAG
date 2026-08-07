// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectSearchTests
{
    [Fact]
    public void ExplicitResolutionAcceptsStableIdLabelAndAlias()
    {
        SubjectCatalogRecord catalog = SubjectTestData.Catalog();

        Assert.Equal("subject-hydraulics", SubjectSearchPolicy.ResolveExplicit(catalog, "subject-hydraulics"));
        Assert.Equal("subject-hydraulics", SubjectSearchPolicy.ResolveExplicit(catalog, "Hydraulics"));
        Assert.Equal("subject-hydraulics", SubjectSearchPolicy.ResolveExplicit(catalog, "fluid power"));
        Assert.Null(SubjectSearchPolicy.ResolveExplicit(catalog, "unknown subject"));
    }

    [Fact]
    public void InferenceIsDeterministicAndBoostsWithoutFiltering()
    {
        SubjectCatalogRecord catalog = SubjectTestData.Catalog();
        string? inferred = SubjectSearchPolicy.Infer(catalog, "How do I troubleshoot fluid power pressure?");
        DocChunk matching = SubjectTestData.Chunk("matching", "revision-matching");
        DocChunk other = SubjectTestData.Chunk("other",
                                               "revision-other",
                                               ["subject-electrical"]);
        DocChunk legacy = SubjectTestData.Chunk("legacy", "revision-legacy", [], null);

        Assert.Equal("subject-hydraulics", inferred);
        Assert.Equal(SubjectClassificationLimits.InferredSubjectBoost,
                     SubjectSearchPolicy.GetBoost(matching, inferred));
        Assert.Equal(0.0, SubjectSearchPolicy.GetBoost(other, inferred));
        Assert.Equal(0.0, SubjectSearchPolicy.GetBoost(legacy, inferred));
    }

    [Fact]
    public async Task ExplicitFilterCombinesWithCategoryWhileSubjectlessSearchIsUnchanged()
    {
        var provider = new InMemoryBruteForceVectorSearch();
        IReadOnlyList<DocChunk> chunks =
        [
            SubjectTestData.Chunk("hydraulics-howto", "revision-1"),
            SubjectTestData.Chunk("electrical-howto", "revision-2", ["subject-electrical"]),
            SubjectTestData.Chunk("hydraulics-overview", "revision-3") with { Category = DocCategory.Overview }
        ];
        await provider.IndexChunksAsync(null,
                                        "manual-library",
                                        "2026-08-04",
                                        chunks,
                                        TestContext.Current.CancellationToken);

        IReadOnlyList<VectorSearchResult> filtered = await provider.SearchAsync(
                                                          [1.0f, 0.0f],
                                                          new VectorSearchFilter
                                                              {
                                                                  LibraryId = "manual-library",
                                                                  Version = "2026-08-04",
                                                                  Category = DocCategory.HowTo,
                                                                  SubjectId = "subject-hydraulics"
                                                              },
                                                          10,
                                                          TestContext.Current.CancellationToken);
        IReadOnlyList<VectorSearchResult> legacy = await provider.SearchAsync(
                                                        [1.0f, 0.0f],
                                                        new VectorSearchFilter
                                                            {
                                                                LibraryId = "manual-library",
                                                                Version = "2026-08-04"
                                                            },
                                                        10,
                                                        TestContext.Current.CancellationToken);

        Assert.Equal("hydraulics-howto", Assert.Single(filtered).Chunk.Id);
        Assert.Equal(3, legacy.Count);
    }

    [Fact]
    public async Task EnrichmentUsesOneBulkAssignmentReadAndOneBulkCatalogRead()
    {
        var assignments = new InMemorySubjectAssignmentRepository();
        assignments.Seed(new SubjectAssignmentRecord
                             {
                                 Id = "revision-1/subjects",
                                 LibraryId = "manual-library",
                                 Version = "2026-08-04",
                                 ScanRunId = "scan-1",
                                 DocumentId = "document-1",
                                 DocumentRevisionId = "revision-1",
                                 TaxonomyVersion = "taxonomy-000001",
                                 Primary = new SubjectSelection
                                     {
                                         SubjectId = "subject-hydraulics",
                                         Confidence = 0.42f,
                                         Evidence = ["pump"]
                                     },
                                 Secondary =
                                 [
                                     new SubjectSelection
                                         {
                                             SubjectId = "subject-safety",
                                             Confidence = 0.73f,
                                             Evidence = ["lockout"]
                                         }
                                 ],
                                 NeedsReview = true,
                                 Provenance = SubjectTestData.Provenance(SubjectClassificationPrompt.PromptVersion)
                             });
        var catalogs = new InMemorySubjectCatalogRepository();
        catalogs.Seed(SubjectTestData.Catalog());
        var enricher = new SubjectSearchEnricher(assignments, catalogs);
        IReadOnlyList<DocChunk> chunks =
        [
            SubjectTestData.Chunk("chunk-a", "revision-1"),
            SubjectTestData.Chunk("chunk-b", "revision-1")
        ];

        IReadOnlyDictionary<string, SubjectSearchMetadata> result = await enricher.EnrichAsync(
            chunks,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, assignments.BulkReadCallCount);
        Assert.Equal(1, catalogs.GetManyCallCount);
        Assert.Equal(2, result.Count);
        SubjectSearchMetadata metadata = result["chunk-a"];
        Assert.True(metadata.NeedsReview);
        Assert.Equal("taxonomy-000001", metadata.TaxonomyVersion);
        Assert.Collection(metadata.Subjects,
                          primary =>
                          {
                              Assert.Equal("Hydraulics", primary.Label);
                              Assert.Equal("primary", primary.Role);
                              Assert.Equal(["pump"], primary.Evidence);
                          },
                          secondary =>
                          {
                              Assert.Equal("Safety", secondary.Label);
                              Assert.Equal("secondary", secondary.Role);
                              Assert.Equal(["lockout"], secondary.Evidence);
                          });
    }
}
