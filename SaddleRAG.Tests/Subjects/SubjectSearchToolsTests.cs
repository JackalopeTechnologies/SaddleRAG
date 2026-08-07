// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectSearchToolsTests
{
    [Fact]
    public async Task ExplicitSubjectResolvesIntoVectorFilterAndEnrichedJson()
    {
        SearchFixture fixture = MakeFixture();
        DocChunk chunk = MakeChunk("hydraulics", ["subject-hydraulics"], "revision-1");
        fixture.Vector.SearchAsync(Arg.Any<float[]>(),
                                   Arg.Any<VectorSearchFilter>(),
                                   Arg.Any<int>(),
                                   Arg.Any<CancellationToken>())
               .Returns([new VectorSearchResult { Chunk = chunk, Score = 0.90f }]);
        fixture.Assignments.GetByDocumentRevisionIdsAsync(Arg.Any<IReadOnlyCollection<string>>(),
                                                          Arg.Any<CancellationToken>())
               .Returns(
                   [
                       new SubjectAssignmentRecord
                           {
                               Id = "lib/v1/revision-1/subjects",
                               LibraryId = "lib",
                               Version = "v1",
                               ScanRunId = "scan-1",
                               DocumentId = "document-1",
                               DocumentRevisionId = "revision-1",
                               TaxonomyVersion = "taxonomy-000001",
                               Primary = new SubjectSelection
                                   {
                                       SubjectId = "subject-hydraulics",
                                       Confidence = 0.92f,
                                       Evidence = ["pump service"]
                                   },
                               Secondary =
                               [
                                   new SubjectSelection
                                       {
                                           SubjectId = "subject-safety",
                                           Confidence = 0.70f,
                                           Evidence = ["lockout"]
                                       }
                               ],
                               NeedsReview = true,
                               Provenance = SubjectTestData.Provenance("subject-assignment-v1")
                           }
                   ]);

        string json = await SearchAsync(fixture, "pump repair", "Hydraulics");

        await fixture.Vector.Received(1)
                     .SearchAsync(Arg.Any<float[]>(),
                                  Arg.Is<VectorSearchFilter>(filter => filter != null &&
                                                                       filter.SubjectId == "subject-hydraulics" &&
                                                                       filter.LibraryId == "lib" &&
                                                                       filter.Version == "v1"),
                                  Arg.Any<int>(),
                                  Arg.Any<CancellationToken>());
        var root = JsonNode.Parse(json)!.AsObject();
        var result = root["Results"]!.AsArray()[0]!.AsObject();
        Assert.True(result["NeedsReview"]!.GetValue<bool>());
        Assert.Equal("taxonomy-000001", result["SubjectTaxonomyVersion"]!.GetValue<string>());
        var subjects = result["Subjects"]!.AsArray();
        Assert.Equal("Hydraulics", subjects[0]!["Label"]!.GetValue<string>());
        Assert.Equal("primary", subjects[0]!["Role"]!.GetValue<string>());
        Assert.Equal(0.92f, subjects[0]!["Confidence"]!.GetValue<float>());
        Assert.Equal("pump service", subjects[0]!["Evidence"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("Safety", subjects[1]!["Label"]!.GetValue<string>());
        Assert.Equal("secondary", subjects[1]!["Role"]!.GetValue<string>());
        Assert.Equal("subject-hydraulics",
                     root["Strategy"]!["ExplicitSubjectId"]!.GetValue<string>());
    }

    [Fact]
    public async Task InferredAliasOnlyBoostsAndNeverEntersVectorFilter()
    {
        SearchFixture fixture = MakeFixture();
        DocChunk other = MakeChunk("electrical", ["subject-electrical"], "revision-2");
        DocChunk matching = MakeChunk("hydraulics", ["subject-hydraulics"], "revision-1");
        fixture.Vector.SearchAsync(Arg.Any<float[]>(),
                                   Arg.Any<VectorSearchFilter>(),
                                   Arg.Any<int>(),
                                   Arg.Any<CancellationToken>())
               .Returns(
                   [
                       new VectorSearchResult { Chunk = other, Score = 0.82f },
                       new VectorSearchResult { Chunk = matching, Score = 0.80f }
                   ]);

        string json = await SearchAsync(fixture, "fluid power pressure", subject: null);

        await fixture.Vector.Received(1)
                     .SearchAsync(Arg.Any<float[]>(),
                                  Arg.Is<VectorSearchFilter>(filter => filter != null && filter.SubjectId == null),
                                  Arg.Any<int>(),
                                  Arg.Any<CancellationToken>());
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("page-hydraulics",
                     root["Results"]!.AsArray()[0]!["PageTitle"]!.GetValue<string>());
        Assert.Equal("subject-hydraulics",
                     root["Strategy"]!["InferredSubjectId"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownExplicitSubjectErrorsBeforeEmbedding()
    {
        SearchFixture fixture = MakeFixture();

        string json = await SearchAsync(fixture, "pump repair", "not-a-subject");

        Assert.Contains("Error", json, StringComparison.Ordinal);
        await fixture.Embedding.DidNotReceiveWithAnyArgs()
                     .EmbedAsync(Arg.Any<IReadOnlyList<string>>(),
                                 Arg.Any<EmbedRole>(),
                                 Arg.Any<CancellationToken>());
        await fixture.Vector.DidNotReceiveWithAnyArgs()
                     .SearchAsync(Arg.Any<float[]>(),
                                  Arg.Any<VectorSearchFilter>(),
                                  Arg.Any<int>(),
                                  Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AmbiguousExplicitAliasErrorsBeforeEmbedding()
    {
        SubjectCatalogRecord ambiguous = SubjectTestData.Catalog() with
                                             {
                                                 LibraryId = "lib",
                                                 Id = "lib/taxonomy-000001",
                                                 Concepts = SubjectTestData.Catalog().Concepts.Append(
                                                     new SubjectConcept
                                                         {
                                                             Id = "subject-pneumatics",
                                                             Label = "Pneumatics",
                                                             Aliases = ["fluid power"],
                                                             Description = "Compressed-air power."
                                                         }).ToList()
                                             };
        SearchFixture fixture = MakeFixture(ambiguous);

        string json = await SearchAsync(fixture, "repair", "fluid power");

        Assert.Contains("Error", json, StringComparison.Ordinal);
        await fixture.Embedding.DidNotReceiveWithAnyArgs()
                     .EmbedAsync(Arg.Any<IReadOnlyList<string>>(),
                                 Arg.Any<EmbedRole>(),
                                 Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IdentifierFastPathCannotInjectChunkOutsideExplicitSubject()
    {
        SearchFixture fixture = MakeFixture();
        DocChunk matching = MakeChunk("matching", ["subject-hydraulics"], "revision-1") with
                                { QualifiedName = "PumpController" };
        DocChunk mismatched = MakeChunk("mismatched", ["subject-electrical"], "revision-2") with
                                  { QualifiedName = "PumpController" };
        fixture.Vector.SearchAsync(Arg.Any<float[]>(),
                                   Arg.Any<VectorSearchFilter>(),
                                   Arg.Any<int>(),
                                   Arg.Any<CancellationToken>())
               .Returns([new VectorSearchResult { Chunk = matching, Score = 0.9f }]);
        fixture.Chunks.FindByQualifiedNameAsync("lib",
                                                "v1",
                                                "PumpController",
                                                Arg.Any<CancellationToken>())
               .Returns([mismatched]);

        string json = await SearchAsync(fixture, "PumpController", "Hydraulics");

        Assert.Contains("page-matching", json, StringComparison.Ordinal);
        Assert.DoesNotContain("page-mismatched", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacySubjectlessSearchKeepsLegacyJsonShape()
    {
        SearchFixture fixture = MakeFixture(catalog: null, taxonomyVersion: null);
        DocChunk legacy = MakeChunk("legacy", [], "revision-legacy") with
                              {
                                  SubjectTaxonomyVersion = null,
                                  DocumentSource = null
                              };
        fixture.Vector.SearchAsync(Arg.Any<float[]>(),
                                   Arg.Any<VectorSearchFilter>(),
                                   Arg.Any<int>(),
                                   Arg.Any<CancellationToken>())
               .Returns([new VectorSearchResult { Chunk = legacy, Score = 0.9f }]);

        string json = await SearchAsync(fixture, "general maintenance", subject: null);

        var root = JsonNode.Parse(json)!.AsObject();
        var result = root["Results"]!.AsArray()[0]!.AsObject();
        Assert.False(result.ContainsKey("Subjects"));
        Assert.False(result.ContainsKey("NeedsReview"));
        Assert.False(result.ContainsKey("SubjectTaxonomyVersion"));
        Assert.False(root["Strategy"]!.AsObject().ContainsKey("ExplicitSubjectId"));
        Assert.False(root["Strategy"]!.AsObject().ContainsKey("InferredSubjectId"));
        await fixture.Vector.Received(1)
                     .SearchAsync(Arg.Any<float[]>(),
                                  Arg.Is<VectorSearchFilter>(filter => filter != null && filter.SubjectId == null),
                                  Arg.Any<int>(),
                                  Arg.Any<CancellationToken>());
    }

    private static async Task<string> SearchAsync(SearchFixture fixture, string query, string? subject) =>
        await SearchTools.SearchDocs(fixture.Vector,
                                     fixture.Embedding,
                                     Substitute.For<IReRanker>(),
                                     fixture.Factory,
                                     Options.Create(new RankingSettings { ReRankerStrategy = ReRankerStrategy.Off }),
                                     Substitute.For<IQueryMetrics>(),
                                     NullLogger<SearchTools.SearchToolsLog>.Instance,
                                     query,
                                     library: "lib",
                                     category: null,
                                     subject: subject,
                                     version: "v1",
                                     maxResults: 5,
                                     profile: null,
                                     ct: TestContext.Current.CancellationToken);

    private static SearchFixture MakeFixture(SubjectCatalogRecord? catalog = null,
                                             string? taxonomyVersion = "taxonomy-000001")
    {
        SubjectCatalogRecord? effectiveCatalog = catalog ?? (taxonomyVersion == null ? null : SubjectTestData.Catalog() with
            {
                Id = "lib/taxonomy-000001",
                LibraryId = "lib"
            });
        IReadOnlyList<SubjectCatalogRecord> catalogRows = effectiveCatalog == null
                                                              ? []
                                                              : [effectiveCatalog];
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var libraries = Substitute.For<ILibraryRepository>();
        var catalogs = Substitute.For<ISubjectCatalogRepository>();
        var assignments = Substitute.For<ISubjectAssignmentRepository>();
        var chunks = Substitute.For<IChunkRepository>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        var shards = Substitute.For<IBm25ShardRepository>();
        var vector = Substitute.For<IVectorSearchProvider>();
        var embedding = Substitute.For<IEmbeddingProvider>();

        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraries);
        factory.GetSubjectCatalogRepository(Arg.Any<string?>()).Returns(catalogs);
        factory.GetSubjectAssignmentRepository(Arg.Any<string?>()).Returns(assignments);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunks);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexes);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(shards);
        libraries.GetLibraryAsync("lib", Arg.Any<CancellationToken>())
                 .Returns(new LibraryRecord
                              {
                                  Id = "lib",
                                  Name = "Manual library",
                                  Hint = "manuals",
                                  CurrentVersion = "v1",
                                  AllVersions = ["v1"]
                              });
        libraries.GetVersionAsync("lib", "v1", Arg.Any<CancellationToken>())
                 .Returns(new LibraryVersionRecord
                              {
                                  Id = "lib/v1",
                                  LibraryId = "lib",
                                  Version = "v1",
                                  ScrapedAt = SubjectTestData.GeneratedAtUtc,
                                  PageCount = 1,
                                  ChunkCount = 1,
                                  EmbeddingProviderId = "capture",
                                  EmbeddingModelName = "capture-model",
                                  EmbeddingDimensions = 2,
                                  PublicationState = VersionPublicationState.Published,
                                  SubjectTaxonomyVersion = taxonomyVersion
                              });
        catalogs.GetAsync("lib", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(effectiveCatalog);
        catalogs.GetLatestAsync("lib", Arg.Any<CancellationToken>()).Returns(effectiveCatalog);
        catalogs.GetManyAsync(Arg.Any<IReadOnlyCollection<SubjectCatalogKey>>(),
                              Arg.Any<CancellationToken>())
                .Returns(catalogRows);
        indexes.GetAsync("lib", "v1", Arg.Any<CancellationToken>()).Returns((LibraryIndex?)null);
        assignments.GetByDocumentRevisionIdsAsync(Arg.Any<IReadOnlyCollection<string>>(),
                                                  Arg.Any<CancellationToken>())
                   .Returns(Array.Empty<SubjectAssignmentRecord>());
        embedding.EmbedAsync(Arg.Any<IReadOnlyList<string>>(),
                             EmbedRole.Query,
                             Arg.Any<CancellationToken>())
                 .Returns([new[] { 1.0f, 0.0f }]);
        vector.SearchAsync(Arg.Any<float[]>(),
                           Arg.Any<VectorSearchFilter>(),
                           Arg.Any<int>(),
                           Arg.Any<CancellationToken>())
              .Returns(Array.Empty<VectorSearchResult>());

        return new SearchFixture(factory, libraries, catalogs, assignments, chunks, vector, embedding);
    }

    private static DocChunk MakeChunk(string id, IReadOnlyList<string> subjectIds, string revisionId) =>
        new()
            {
                Id = id,
                LibraryId = "lib",
                Version = "v1",
                PageUrl = $"saddlerag://lib/chunks/{id}",
                PageTitle = $"page-{id}",
                Category = DocCategory.HowTo,
                Content = $"content-{id}",
                Embedding = [1.0f, 0.0f],
                TokenCount = 2,
                SubjectIds = subjectIds,
                SubjectTaxonomyVersion = subjectIds.Count == 0 ? null : "taxonomy-000001",
                DocumentSource = new DocumentProvenance
                    {
                        DocumentId = $"document-{id}",
                        RevisionId = revisionId,
                        SourceUri = $"saddlerag://lib/documents/document-{id}",
                        RelativePath = $"manuals/{id}.pdf"
                    }
            };

    private sealed record SearchFixture(RepositoryFactory Factory,
                                        ILibraryRepository Libraries,
                                        ISubjectCatalogRepository Catalogs,
                                        ISubjectAssignmentRepository Assignments,
                                        IChunkRepository Chunks,
                                        IVectorSearchProvider Vector,
                                        IEmbeddingProvider Embedding);
}
