// ScrapeJobRunnerProfileTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;

namespace SaddleRAG.Tests.Ingestion;

public sealed class ScrapeJobRunnerProfileTests
{
    [Fact]
    public async Task ImportedIndexReloadReadsChunksFromTheSuppliedNamedProfile()
    {
        var defaultChunks = Substitute.For<IChunkRepository>();
        var namedChunks = Substitute.For<IChunkRepository>();
        DocChunk namedEmbedded = Chunk(NamedEmbeddedChunkId, [1.0f]);
        DocChunk namedUnembedded = Chunk(NamedUnembeddedChunkId, embedding: null);
        DocChunk defaultEmbedded = Chunk(DefaultEmbeddedChunkId, [2.0f]);
        namedChunks.GetChunksAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                   .Returns([namedEmbedded, namedUnembedded]);
        defaultChunks.GetChunksAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                     .Returns([defaultEmbedded]);
        var factory = Substitute.For<RepositoryFactory>([null!]);
        factory.GetChunkRepository(Profile).Returns(namedChunks);
        factory.GetChunkRepository(Arg.Is<string?>(candidate => candidate == null)).Returns(defaultChunks);
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var runner = new ScrapeJobRunner(null!,
                                         defaultChunks,
                                         vectorSearch,
                                         Substitute.For<ILibraryRepository>(),
                                         NullLogger<ScrapeJobRunner>.Instance,
                                         factory,
                                         Substitute.For<IJobCancellationRegistry>(),
                                         lifetime);

        await runner.ReloadIndexForLibraryAsync(Profile,
                                                LibraryId,
                                                Version,
                                                TestContext.Current.CancellationToken);

        factory.Received(requiredNumberOfCalls: 1).GetChunkRepository(Profile);
        await namedChunks.Received(requiredNumberOfCalls: 1)
                         .GetChunksAsync(LibraryId, Version, TestContext.Current.CancellationToken);
        await defaultChunks.DidNotReceive()
                           .GetChunksAsync(Arg.Any<string>(),
                                           Arg.Any<string>(),
                                           Arg.Any<CancellationToken>());
        await vectorSearch.Received(requiredNumberOfCalls: 1)
                          .IndexChunksAsync(Profile,
                                             LibraryId,
                                             Version,
                                             Arg.Is<IReadOnlyList<DocChunk>>(chunks =>
                                                 chunks != null &&
                                                 chunks.Count == 1 &&
                                                 chunks[index: 0] != null &&
                                                 chunks[index: 0].Id == NamedEmbeddedChunkId),
                                             TestContext.Current.CancellationToken);
    }

    private static DocChunk Chunk(string id, float[]? embedding) => new()
        {
            Id = id,
            LibraryId = LibraryId,
            Version = Version,
            PageUrl = $"https://example.test/{id}",
            PageTitle = id,
            Category = DocCategory.HowTo,
            Content = id,
            Embedding = embedding,
            TokenCount = 1
        };

    private const string Profile = "named-profile";
    private const string LibraryId = "imported-library";
    private const string Version = "v1";
    private const string NamedEmbeddedChunkId = "named-embedded";
    private const string NamedUnembeddedChunkId = "named-unembedded";
    private const string DefaultEmbeddedChunkId = "default-embedded";
}
