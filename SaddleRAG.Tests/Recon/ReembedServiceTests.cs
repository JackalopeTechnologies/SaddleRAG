// ReembedServiceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Recon;

#endregion

namespace SaddleRAG.Tests.Recon;

public sealed class ReembedServiceTests
{
    [Fact]
    public async Task ReturnsNothingToDoWhenNoChunks()
    {
        var embeddingProvider = MakeEmbeddingProvider();
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(Array.Empty<DocChunk>());
        libraryRepo.GetVersionAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeVersion("ollama", "nomic-embed-text", dims: 768));

        var service = new ReembedService(embeddingProvider, vectorSearch, NullLogger<ReembedService>.Instance);

        var result = await service.ReembedAsync(profile: null,
                                                 chunkRepo,
                                                 libraryRepo,
                                                 "lib",
                                                 "1.0",
                                                 new ReembedOptions(),
                                                 onProgress: null,
                                                 ct: TestContext.Current.CancellationToken
                                                );

        Assert.True(result.NothingToDo);
        Assert.Equal(expected: 0, result.Processed);
        Assert.Equal("ollama", result.PreviousEmbeddingProviderId);
        Assert.Equal("nomic-embed-text", result.PreviousEmbeddingModelName);
        Assert.Equal(expected: 768, result.PreviousEmbeddingDimensions);
        await embeddingProvider.DidNotReceive()
                               .EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await chunkRepo.DidNotReceive()
                       .UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DryRunDoesNotEmbedOrPersist()
    {
        var embeddingProvider = MakeEmbeddingProvider();
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        var chunk = MakeChunk("c1", "hello world");
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new[] { chunk });
        libraryRepo.GetVersionAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeVersion("ollama", "nomic-embed-text", dims: 768));

        var service = new ReembedService(embeddingProvider, vectorSearch, NullLogger<ReembedService>.Instance);

        var result = await service.ReembedAsync(profile: null,
                                                 chunkRepo,
                                                 libraryRepo,
                                                 "lib",
                                                 "1.0",
                                                 new ReembedOptions { DryRun = true },
                                                 onProgress: null,
                                                 ct: TestContext.Current.CancellationToken
                                                );

        Assert.True(result.DryRun);
        Assert.Equal(expected: 1, result.Processed);
        await embeddingProvider.DidNotReceive()
                               .EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await chunkRepo.DidNotReceive()
                       .UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());
        await vectorSearch.DidNotReceive()
                          .IndexChunksAsync(Arg.Any<string?>(),
                                            Arg.Any<string>(),
                                            Arg.Any<string>(),
                                            Arg.Any<IReadOnlyList<DocChunk>>(),
                                            Arg.Any<CancellationToken>()
                                           );
        await libraryRepo.DidNotReceive()
                         .UpsertVersionAsync(Arg.Any<LibraryVersionRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RealRunEmbedsPersistsAndUpdatesVersionMetadata()
    {
        var embeddingProvider = MakeEmbeddingProvider("tei", "nomic-ai/nomic-embed-text-v1.5", dims: 768);
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        var c1 = MakeChunk("c1", "alpha");
        var c2 = MakeChunk("c2", "beta");
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new[] { c1, c2 });
        libraryRepo.GetVersionAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeVersion("ollama", "nomic-embed-text", dims: 768));

        var service = new ReembedService(embeddingProvider, vectorSearch, NullLogger<ReembedService>.Instance);

        var result = await service.ReembedAsync(profile: null,
                                                 chunkRepo,
                                                 libraryRepo,
                                                 "lib",
                                                 "1.0",
                                                 new ReembedOptions(),
                                                 onProgress: null,
                                                 ct: TestContext.Current.CancellationToken
                                                );

        Assert.False(result.DryRun);
        Assert.False(result.NothingToDo);
        Assert.Equal(expected: 2, result.Processed);
        Assert.Equal("tei", result.EmbeddingProviderId);
        Assert.Equal("nomic-ai/nomic-embed-text-v1.5", result.EmbeddingModelName);
        Assert.Equal("ollama", result.PreviousEmbeddingProviderId);
        Assert.Equal("nomic-embed-text", result.PreviousEmbeddingModelName);

        await embeddingProvider.Received(requiredNumberOfCalls: 1)
                               .EmbedAsync(Arg.Is<IReadOnlyList<string>>(x => x.Count == 2),
                                           Arg.Any<CancellationToken>()
                                          );
        await chunkRepo.Received(requiredNumberOfCalls: 1)
                       .UpsertChunksAsync(Arg.Is<IReadOnlyList<DocChunk>>(x => x.Count == 2),
                                          Arg.Any<CancellationToken>()
                                         );
        await vectorSearch.Received(requiredNumberOfCalls: 1)
                          .IndexChunksAsync(null,
                                            "lib",
                                            "1.0",
                                            Arg.Is<IReadOnlyList<DocChunk>>(x => x.Count == 2),
                                            Arg.Any<CancellationToken>()
                                           );
        await libraryRepo.Received(requiredNumberOfCalls: 1)
                         .UpsertVersionAsync(Arg.Is<LibraryVersionRecord>(v =>
                                                                              v.EmbeddingProviderId == "tei" &&
                                                                              v.EmbeddingModelName ==
                                                                              "nomic-ai/nomic-embed-text-v1.5"
                                                                         ),
                                             Arg.Any<CancellationToken>()
                                            );
    }

    [Fact]
    public async Task MaxChunksCapsTheBatch()
    {
        var embeddingProvider = MakeEmbeddingProvider();
        var vectorSearch = Substitute.For<IVectorSearchProvider>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        var chunks = Enumerable.Range(start: 0, count: 5).Select(i => MakeChunk($"c{i}", $"text{i}")).ToArray();
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(chunks);
        libraryRepo.GetVersionAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeVersion("ollama", "nomic-embed-text", dims: 768));

        var service = new ReembedService(embeddingProvider, vectorSearch, NullLogger<ReembedService>.Instance);

        var result = await service.ReembedAsync(profile: null,
                                                 chunkRepo,
                                                 libraryRepo,
                                                 "lib",
                                                 "1.0",
                                                 new ReembedOptions { MaxChunks = 2 },
                                                 onProgress: null,
                                                 ct: TestContext.Current.CancellationToken
                                                );

        Assert.Equal(expected: 2, result.Processed);
        await embeddingProvider.Received()
                               .EmbedAsync(Arg.Is<IReadOnlyList<string>>(x => x.Count == 2),
                                           Arg.Any<CancellationToken>()
                                          );
    }

    private static IEmbeddingProvider MakeEmbeddingProvider(string providerId = "ollama",
                                                             string modelName = "nomic-embed-text",
                                                             int dims = 768)
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.ProviderId.Returns(providerId);
        provider.ModelName.Returns(modelName);
        provider.Dimensions.Returns(dims);
        provider.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var texts = call.Arg<IReadOnlyList<string>>();
                    var vectors = new float[texts.Count][];
                    for(var i = 0; i < texts.Count; i++)
                        vectors[i] = new float[] { 0.1f, 0.2f, 0.3f };
                    return vectors;
                });
        return provider;
    }

    private static DocChunk MakeChunk(string id, string content)
    {
        var chunk = new DocChunk
                        {
                            Id = id,
                            LibraryId = "lib",
                            Version = "1.0",
                            PageUrl = "https://example.com/page",
                            PageTitle = "Page",
                            Category = DocCategory.ApiReference,
                            Content = content,
                            Embedding = new float[] { 0f, 0f, 0f }
                        };
        return chunk;
    }

    private static LibraryVersionRecord MakeVersion(string providerId, string modelName, int dims)
    {
        var version = new LibraryVersionRecord
                          {
                              Id = "lib-1.0",
                              LibraryId = "lib",
                              Version = "1.0",
                              ScrapedAt = DateTime.UtcNow,
                              PageCount = 1,
                              ChunkCount = 1,
                              EmbeddingProviderId = providerId,
                              EmbeddingModelName = modelName,
                              EmbeddingDimensions = dims
                          };
        return version;
    }
}
