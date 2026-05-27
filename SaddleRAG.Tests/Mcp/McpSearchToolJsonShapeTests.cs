// McpSearchToolJsonShapeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     JSON-shape guards for the three search-family MCP tools that
///     consumers (Claude Code, agents) parse hot-path. The earlier shape
///     tests deferred these because they require a deeper provider
///     fanout (IEmbeddingProvider + IVectorSearchProvider + IReRanker +
///     IQueryMetrics + RankingSettings); this file pays that one-time
///     cost so all three are now pinned at the field-name level.
/// </summary>
public sealed class McpSearchToolJsonShapeTests
{
    private static DocChunk Chunk(string id,
                                  DocCategory category = DocCategory.HowTo,
                                  string? qualifiedName = null) =>
        new DocChunk
            {
                Id = id,
                LibraryId = "lib",
                Version = "v1",
                PageUrl = $"https://example.test/{id}",
                PageTitle = $"page-{id}",
                SectionPath = $"section/{id}",
                Category = category,
                Content = $"content-{id}",
                QualifiedName = qualifiedName,
                CodeLanguage = "csharp"
            };

    private static LibraryRecord NewLibrary(string id = "lib", string version = "v1") =>
        new LibraryRecord
            {
                Id = id,
                Name = id,
                Hint = id,
                CurrentVersion = version,
                AllVersions = [version]
            };

    private static IEmbeddingProvider StubEmbeddingProvider()
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.ProviderId.Returns("stub");
        provider.ModelName.Returns("stub-model");
        provider.Dimensions.Returns(4);
        provider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                         {
                             var texts = call.Arg<IReadOnlyList<string>>();
                             var result = new float[texts.Count][];
                             for(var i = 0; i < texts.Count; i++)
                                 result[i] = [0.1f, 0.2f, 0.3f, 0.4f];
                             return Task.FromResult(result);
                         }
                        );
        return provider;
    }

    private static IReRanker StubReRanker() => Substitute.For<IReRanker>();

    private static IQueryMetrics StubMetrics()
    {
        var metrics = Substitute.For<IQueryMetrics>();
        metrics.ProcessStartedUtc.Returns(DateTime.UtcNow);
        return metrics;
    }

    private static (RepositoryFactory factory,
        ILibraryRepository libraryRepo,
        IChunkRepository chunkRepo,
        ILibraryIndexRepository libraryIndexRepo,
        ILibraryProfileRepository libraryProfileRepo,
        IBm25ShardRepository bm25ShardRepo) MakeFactoryWithLibrary(string? library = "lib", string version = "v1")
    {
        var factory = Substitute.For<RepositoryFactory>([null]);
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var libraryIndexRepo = Substitute.For<ILibraryIndexRepository>();
        var libraryProfileRepo = Substitute.For<ILibraryProfileRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();

        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(libraryIndexRepo);
        factory.GetLibraryProfileRepository(Arg.Any<string?>()).Returns(libraryProfileRepo);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(bm25ShardRepo);

        libraryRepo.GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryRecord?>(library is null ? null : NewLibrary(library, version)));
        libraryIndexRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult<LibraryIndex?>(null));

        return (factory, libraryRepo, chunkRepo, libraryIndexRepo, libraryProfileRepo, bm25ShardRepo);
    }

    [Fact]
    public async Task SearchDocsShapeIsResultsAndTimingAndStrategyObject()
    {
        (var factory, var _, var _, var _, var _, var _) = MakeFactoryWithLibrary();

        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        vectorSearch.SearchAsync(Arg.Any<float[]>(),
                                 Arg.Any<VectorSearchFilter>(),
                                 Arg.Any<int>(),
                                 Arg.Any<CancellationToken>()
                                )
                    .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>([
                                         new VectorSearchResult { Chunk = Chunk("c1"), Score = 0.9f },
                                         new VectorSearchResult { Chunk = Chunk("c2"), Score = 0.8f }
                                     ]
                                    )
                            );

        var json = await SearchTools.SearchDocs(vectorSearch,
                                                StubEmbeddingProvider(),
                                                StubReRanker(),
                                                factory,
                                                Options.Create(new RankingSettings()),
                                                StubMetrics(),
                                                NullLogger<SearchTools.SearchToolsLog>.Instance,
                                                query: "hello",
                                                library: "lib",
                                                category: null,
                                                version: null,
                                                maxResults: 5,
                                                profile: null,
                                                TestContext.Current.CancellationToken
                                               );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        foreach(var key in SearchDocsTopLevelFields)
            Assert.True(root.ContainsKey(key), $"search_docs missing '{key}' top-level field");

        var timing = root["Timing"] as JsonObject;
        Assert.NotNull(timing);
        foreach(var key in SearchDocsTimingFields)
            Assert.True(timing.ContainsKey(key), $"search_docs Timing missing '{key}' field");

        var strategy = root["Strategy"] as JsonObject;
        Assert.NotNull(strategy);
        foreach(var key in SearchDocsStrategyFields)
            Assert.True(strategy.ContainsKey(key), $"search_docs Strategy missing '{key}' field");

        var results = root["Results"] as JsonArray;
        Assert.NotNull(results);
        Assert.NotEmpty(results);
        var first = results[0] as JsonObject;
        Assert.NotNull(first);
        foreach(var key in SearchDocsResultEntryFields)
            Assert.True(first.ContainsKey(key), $"search_docs Results entry missing '{key}' field");
    }

    [Fact]
    public async Task SearchDocsReturnsLibraryNotFoundErrorShapeWhenLibraryMissing()
    {
        (var factory, var _, var _, var _, var _, var _) = MakeFactoryWithLibrary(library: null);

        var json = await SearchTools.SearchDocs(Substitute.For<IVectorSearchProvider>(),
                                                StubEmbeddingProvider(),
                                                StubReRanker(),
                                                factory,
                                                Options.Create(new RankingSettings()),
                                                StubMetrics(),
                                                NullLogger<SearchTools.SearchToolsLog>.Instance,
                                                query: "hello",
                                                library: "missing",
                                                category: null,
                                                version: null,
                                                maxResults: 5,
                                                profile: null,
                                                TestContext.Current.CancellationToken
                                               );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root.ContainsKey("Error"));
        Assert.Contains("not found", root["Error"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetLibraryOverviewShapeIsArrayOfChunkProjectionWithScore()
    {
        (var factory, var _, var _, var _, var _, var _) = MakeFactoryWithLibrary();

        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        vectorSearch.SearchAsync(Arg.Any<float[]>(),
                                 Arg.Any<VectorSearchFilter>(),
                                 Arg.Any<int>(),
                                 Arg.Any<CancellationToken>()
                                )
                    .Returns(Task.FromResult<IReadOnlyList<VectorSearchResult>>([
                                         new VectorSearchResult
                                             { Chunk = Chunk("c1", DocCategory.Overview), Score = 0.95f }
                                     ]
                                    )
                            );

        var json = await SearchTools.GetLibraryOverview(vectorSearch,
                                                        StubEmbeddingProvider(),
                                                        factory,
                                                        StubMetrics(),
                                                        library: "lib",
                                                        version: null,
                                                        profile: null,
                                                        TestContext.Current.CancellationToken
                                                       );

        var array = JsonNode.Parse(json) as JsonArray;
        Assert.NotNull(array);
        Assert.Single(array);
        var entry = array[0] as JsonObject;
        Assert.NotNull(entry);
        foreach(var key in OverviewEntryFields)
            Assert.True(entry.ContainsKey(key), $"get_library_overview entry missing '{key}' field");
        Assert.Equal("lib", entry["LibraryId"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetLibraryOverviewReturnsLibraryNotFoundErrorShapeWhenLibraryMissing()
    {
        (var factory, var _, var _, var _, var _, var _) = MakeFactoryWithLibrary(library: null);

        var json = await SearchTools.GetLibraryOverview(Substitute.For<IVectorSearchProvider>(),
                                                        StubEmbeddingProvider(),
                                                        factory,
                                                        StubMetrics(),
                                                        library: "missing",
                                                        version: null,
                                                        profile: null,
                                                        TestContext.Current.CancellationToken
                                                       );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root.ContainsKey("Error"));
    }

    [Fact]
    public async Task GetClassReferenceShapeIsArrayOfChunkProjectionWithQualifiedName()
    {
        (var factory, var _, var chunkRepo, var _, var _, var _) = MakeFactoryWithLibrary();
        chunkRepo.FindByQualifiedNameAsync("lib", "v1", "Foo", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([
                                  Chunk("c1", DocCategory.ApiReference, qualifiedName: "Foo")
                              ]
                             )
                         );

        var json = await SearchTools.GetClassReference(factory,
                                                       className: "Foo",
                                                       library: "lib",
                                                       version: null,
                                                       profile: null,
                                                       TestContext.Current.CancellationToken
                                                      );

        var array = JsonNode.Parse(json) as JsonArray;
        Assert.NotNull(array);
        Assert.Single(array);
        var entry = array[0] as JsonObject;
        Assert.NotNull(entry);
        foreach(var key in ClassReferenceEntryFields)
            Assert.True(entry.ContainsKey(key), $"get_class_reference entry missing '{key}' field");
        Assert.Equal("Foo", entry["QualifiedName"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetClassReferenceReturnsLibraryNotFoundErrorShapeWhenLibraryMissing()
    {
        (var factory, var _, var _, var _, var _, var _) = MakeFactoryWithLibrary(library: null);

        var json = await SearchTools.GetClassReference(factory,
                                                       className: "Foo",
                                                       library: "missing",
                                                       version: null,
                                                       profile: null,
                                                       TestContext.Current.CancellationToken
                                                      );

        var root = JsonNode.Parse(json) as JsonObject;
        Assert.NotNull(root);
        Assert.True(root.ContainsKey("Error"));
    }

    private static readonly string[] SearchDocsTopLevelFields = ["Results", "Timing", "Strategy"];

    private static readonly string[] SearchDocsTimingFields =
        ["EmbedMs", "VectorSearchMs", "Bm25Ms", "ReRankMs", "TotalMs", "CandidateCount", "ReRankCandidateCount"];

    private static readonly string[] SearchDocsStrategyFields =
        ["ReRankerStrategy", "RerankActive", "QueryIsIdentifierShape", "Category", "Bm25Weight"];

    private static readonly string[] SearchDocsResultEntryFields =
        [
            "LibraryId", "Category", "PageTitle", "SectionPath", "PageUrl", "Content", "QualifiedName",
            "CodeLanguage", "RelevanceScore", "VectorScore", "Bm25Score", "RerankScore"
        ];

    private static readonly string[] OverviewEntryFields =
        ["LibraryId", "Category", "PageTitle", "SectionPath", "PageUrl", "Content", "Score"];

    private static readonly string[] ClassReferenceEntryFields =
        ["LibraryId", "QualifiedName", "PageTitle", "SectionPath", "PageUrl", "Content"];
}
