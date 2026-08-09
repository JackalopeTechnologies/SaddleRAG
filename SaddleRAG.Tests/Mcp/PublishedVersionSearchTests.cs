// PublishedVersionSearchTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Mcp;

public sealed class PublishedVersionSearchTests
{
    [Theory]
    [InlineData(VersionPublicationState.Building)]
    [InlineData(VersionPublicationState.Failed)]
    public async Task ExplicitUnpublishedVersionDoesNotCallVectorSearch(VersionPublicationState state)
    {
        var fixture = BuildFixture();
        fixture.Library.GetLibraryAsync("lib", Arg.Any<CancellationToken>()).Returns(Library("lib", "v2"));
        fixture.Library.GetVersionAsync("lib", "v1", Arg.Any<CancellationToken>()).Returns(Version("lib", "v1", state));

        var json = await Search(fixture, library: "lib", version: "v1");

        Assert.Contains("\"Error\"", json);
        await fixture.Vector.DidNotReceiveWithAnyArgs()
                     .SearchAsync(Arg.Any<float[]>(), Arg.Any<VectorSearchFilter>(), Arg.Any<int>(),
                                  Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(VersionPublicationState.Building)]
    [InlineData(VersionPublicationState.Failed)]
    public async Task DefaultCurrentVersionMustBePublished(VersionPublicationState state)
    {
        var fixture = BuildFixture();
        fixture.Library.GetLibraryAsync("lib", Arg.Any<CancellationToken>()).Returns(Library("lib", "v2"));
        fixture.Library.GetVersionAsync("lib", "v2", Arg.Any<CancellationToken>()).Returns(Version("lib", "v2", state));

        await Search(fixture, library: "lib", version: null);

        await fixture.Vector.DidNotReceiveWithAnyArgs()
                     .SearchAsync(Arg.Any<float[]>(), Arg.Any<VectorSearchFilter>(), Arg.Any<int>(),
                                  Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishedExplicitVersionUsesExactLibraryAndVersionFilter()
    {
        var fixture = BuildFixture();
        fixture.Library.GetLibraryAsync("lib", Arg.Any<CancellationToken>()).Returns(Library("lib", "v2"));
        fixture.Library.GetVersionAsync("lib", "v1", Arg.Any<CancellationToken>())
               .Returns(Version("lib", "v1", VersionPublicationState.Published));

        await Search(fixture, library: "lib", version: "v1");

        await fixture.Vector.Received(1)
                     .SearchAsync(Arg.Any<float[]>(),
                                  Arg.Is<VectorSearchFilter>(f => f != null && f.LibraryId == "lib" && f.Version == "v1"),
                                  Arg.Any<int>(),
                                  Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GlobalSearchQueriesOnlyEachLibraryCurrentPublishedVersion()
    {
        var fixture = BuildFixture();
        fixture.Library.GetAllLibrariesAsync(Arg.Any<CancellationToken>())
               .Returns(new[] { Library("a", "v1"), Library("b", "v2"), Library("c", "v3") });
        fixture.Library.GetVersionAsync("a", "v1", Arg.Any<CancellationToken>())
               .Returns(Version("a", "v1", VersionPublicationState.Published));
        fixture.Library.GetVersionAsync("b", "v2", Arg.Any<CancellationToken>())
               .Returns(Version("b", "v2", VersionPublicationState.Building));
        fixture.Library.GetVersionAsync("c", "v3", Arg.Any<CancellationToken>())
               .Returns(Version("c", "v3", VersionPublicationState.Failed));

        await Search(fixture, library: null, version: null);

        await fixture.Vector.Received(1)
                     .SearchAsync(Arg.Any<float[]>(),
                                  Arg.Is<VectorSearchFilter>(f => f != null && f.LibraryId == "a" && f.Version == "v1"),
                                  Arg.Any<int>(),
                                  Arg.Any<CancellationToken>());
        await fixture.Vector.DidNotReceive()
                     .SearchAsync(Arg.Any<float[]>(),
                                  Arg.Is<VectorSearchFilter>(f => f != null &&
                                                                   (f.LibraryId == null || f.LibraryId != "a")),
                                  Arg.Any<int>(),
                                  Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LegacyBsonWithoutPublicationStateDefaultsToPublished()
    {
        var bson = new BsonDocument
                       {
                           ["_id"] = "lib/v1",
                           ["LibraryId"] = "lib",
                           ["Version"] = "v1",
                           ["ScrapedAt"] = DateTime.UtcNow,
                           ["PageCount"] = 1,
                           ["ChunkCount"] = 1,
                           ["EmbeddingProviderId"] = "p",
                           ["EmbeddingModelName"] = "m",
                           ["EmbeddingDimensions"] = 2
                       };

        var version = BsonSerializer.Deserialize<LibraryVersionRecord>(bson);

        Assert.Equal(VersionPublicationState.Published, version.PublicationState);
    }

    private static async Task<string> Search(Fixture fixture, string? library, string? version) =>
        await SearchTools.SearchDocs(fixture.Vector,
                                     fixture.Embedding,
                                     Substitute.For<IReRanker>(),
                                     fixture.Factory,
                                     Options.Create(new RankingSettings()),
                                     Substitute.For<IQueryMetrics>(),
                                     NullLogger<SearchTools.SearchToolsLog>.Instance,
                                     "query",
                                     library,
                                     category: null,
                                     subject: null,
                                     version: version,
                                     maxResults: 5,
                                     profile: null,
                                     ct: TestContext.Current.CancellationToken);

    private static Fixture BuildFixture()
    {
        var factory = Substitute.For<RepositoryFactory>([null!]);
        var library = Substitute.For<ILibraryRepository>();
        var vector = Substitute.For<IVectorSearchProvider>();
        var embedding = Substitute.For<IEmbeddingProvider>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        var shards = Substitute.For<IBm25ShardRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(library);
        factory.GetLibraryIndexRepository(Arg.Any<string?>()).Returns(indexes);
        factory.GetBm25ShardRepository(Arg.Any<string?>()).Returns(shards);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(Substitute.For<IChunkRepository>());
        indexes.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns((LibraryIndex?) null);
        embedding.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<EmbedRole>(), Arg.Any<CancellationToken>())
                 .Returns(new[] { new[] { 1f, 0f } });
        vector.SearchAsync(Arg.Any<float[]>(), Arg.Any<VectorSearchFilter>(), Arg.Any<int>(),
                           Arg.Any<CancellationToken>())
              .Returns(Array.Empty<VectorSearchResult>());
        return new Fixture(factory, library, vector, embedding);
    }

    private static LibraryRecord Library(string id, string current) => new()
        {
            Id = id, Name = id, Hint = "h", CurrentVersion = current, AllVersions = [current]
        };

    private static LibraryVersionRecord Version(string library, string version, VersionPublicationState state) => new()
        {
            Id = $"{library}/{version}", LibraryId = library, Version = version, ScrapedAt = DateTime.UtcNow,
            PageCount = 1, ChunkCount = 1, EmbeddingProviderId = "p", EmbeddingModelName = "m",
            EmbeddingDimensions = 2, PublicationState = state
        };

    private sealed record Fixture(RepositoryFactory Factory,
                                  ILibraryRepository Library,
                                  IVectorSearchProvider Vector,
                                  IEmbeddingProvider Embedding);
}
