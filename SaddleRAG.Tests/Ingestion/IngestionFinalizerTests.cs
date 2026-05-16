// IngestionFinalizerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Suspect;

#endregion

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Verifies IngestionFinalizer's contract: BM25 build skipped when no
///     chunks, prior CodeFenceSymbols + Manifest preserved across a
///     re-scrape, library + version metadata upserted correctly (new vs.
///     existing record), and suspect set/cleared based on detector reasons.
/// </summary>
public sealed class IngestionFinalizerTests
{
    private static ScrapeJob NewJob(string library = "lib", string version = "v1") => new()
        {
            LibraryId = library,
            Version = version,
            RootUrl = "https://example.test/",
            LibraryHint = library,
            AllowedUrlPatterns = []
        };

    private static ScrapeJobRecord NewProgress(int pagesFetched = 3, int chunksCompleted = 9) => new()
        {
            Id = "job-1",
            Job = NewJob(),
            PagesFetched = pagesFetched,
            ChunksCompleted = chunksCompleted,
            PagesCompleted = pagesFetched
        };

    private static DocChunk NewChunk(int index) => new()
        {
            Id = $"c{index}",
            LibraryId = "lib",
            Version = "v1",
            PageUrl = "https://example.test/p",
            PageTitle = "t",
            Category = DocCategory.HowTo,
            Content = $"chunk-{index}-content"
        };

    private static IEmbeddingProvider StubProvider() => StubProviderWith("nomic-v1.5", "nomic-embed-text-v1.5", 768);

    private static IEmbeddingProvider StubProviderWith(string providerId, string modelName, int dimensions)
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.ProviderId.Returns(providerId);
        provider.ModelName.Returns(modelName);
        provider.Dimensions.Returns(dimensions);
        return provider;
    }

    private static IngestionFinalizer NewFinalizer(IChunkRepository? chunks = null,
                                                   IBm25ShardRepository? shards = null,
                                                   ILibraryIndexRepository? indexes = null,
                                                   ILibraryRepository? libraries = null,
                                                   IEmbeddingProvider? provider = null,
                                                   ILibraryProfileRepository? profiles = null,
                                                   SuspectDetector? suspect = null) =>
        new(chunks ?? Substitute.For<IChunkRepository>(),
            shards ?? Substitute.For<IBm25ShardRepository>(),
            indexes ?? Substitute.For<ILibraryIndexRepository>(),
            libraries ?? Substitute.For<ILibraryRepository>(),
            provider ?? StubProvider(),
            profiles ?? Substitute.For<ILibraryProfileRepository>(),
            suspect ?? new SuspectDetector(),
            NullLogger.Instance
           );

    [Fact]
    public async Task BuildBm25IndexAsyncSkipsShardReplacementWhenNoChunksPersisted()
    {
        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));
        var shards = Substitute.For<IBm25ShardRepository>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        var finalizer = NewFinalizer(chunks: chunks, shards: shards, indexes: indexes);

        await finalizer.BuildBm25IndexAsync(NewJob(), TestContext.Current.CancellationToken);

        await shards.DidNotReceiveWithAnyArgs()
                    .ReplaceShardsAsync(Arg.Any<string>(),
                                        Arg.Any<string>(),
                                        Arg.Any<IReadOnlyList<Bm25Shard>>(),
                                        Arg.Any<CancellationToken>()
                                       );
        await indexes.DidNotReceiveWithAnyArgs().UpsertAsync(Arg.Any<LibraryIndex>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildBm25IndexAsyncReplacesShardsAndUpsertsIndexWhenChunksExist()
    {
        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([NewChunk(0), NewChunk(1)]));
        var shards = Substitute.For<IBm25ShardRepository>();
        var indexes = Substitute.For<ILibraryIndexRepository>();
        indexes.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryIndex?>(null));
        var finalizer = NewFinalizer(chunks: chunks, shards: shards, indexes: indexes);

        await finalizer.BuildBm25IndexAsync(NewJob(), TestContext.Current.CancellationToken);

        await shards.Received(1)
                    .ReplaceShardsAsync("lib",
                                        "v1",
                                        Arg.Any<IReadOnlyList<Bm25Shard>>(),
                                        Arg.Any<CancellationToken>()
                                       );
        await indexes.Received(1)
                     .UpsertAsync(Arg.Is<LibraryIndex>(li => li.LibraryId == "lib" && li.Version == "v1"),
                                  Arg.Any<CancellationToken>()
                                 );
    }

    [Fact]
    public async Task BuildBm25IndexAsyncPreservesPriorCodeFenceSymbolsAndManifestOnReScrape()
    {
        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([NewChunk(0)]));

        var priorManifest = new LibraryManifest { LastBuiltUtc = DateTime.UtcNow.AddDays(-3), LastParserVersion = 7 };
        var priorIndex = new LibraryIndex
            {
                Id = LibraryIndexRepository.MakeId("lib", "v1"),
                LibraryId = "lib",
                Version = "v1",
                Bm25 = new Bm25Stats
                           {
                               DocumentCount = 0,
                               ShardCount = 0,
                               AverageDocLength = 0
                           },
                CodeFenceSymbols = ["FooBar", "BazQux"],
                Manifest = priorManifest
            };
        var indexes = Substitute.For<ILibraryIndexRepository>();
        indexes.GetAsync("lib", "v1", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryIndex?>(priorIndex));
        var finalizer = NewFinalizer(chunks: chunks, indexes: indexes);

        await finalizer.BuildBm25IndexAsync(NewJob(), TestContext.Current.CancellationToken);

        await indexes.Received(1)
                     .UpsertAsync(Arg.Is<LibraryIndex>(li => li.CodeFenceSymbols.SequenceEqual(priorIndex.CodeFenceSymbols)
                                                          && li.Manifest == priorManifest),
                                  Arg.Any<CancellationToken>()
                                 );
    }

    [Fact]
    public async Task RunAsyncCreatesNewLibraryRecordWhenLibraryNotPresent()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryRecord?>(null));
        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));
        var finalizer = NewFinalizer(chunks: chunks, libraries: libraries);

        await finalizer.RunAsync(NewJob(library: "newlib", version: "v9"),
                                 NewProgress(),
                                 TestContext.Current.CancellationToken
                                );

        await libraries.Received(1)
                       .UpsertLibraryAsync(Arg.Is<LibraryRecord>(r => r.Id == "newlib"
                                                                   && r.CurrentVersion == "v9"
                                                                   && r.AllVersions.Count == 1
                                                                   && r.AllVersions[0] == "v9"),
                                           Arg.Any<CancellationToken>()
                                          );
    }

    [Fact]
    public async Task RunAsyncUpdatesCurrentVersionAndAppendsNewVersionWhenLibraryExists()
    {
        var existing = new LibraryRecord
            {
                Id = "lib",
                Name = "lib",
                Hint = "lib",
                CurrentVersion = "v1",
                AllVersions = ["v1"]
            };
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetLibraryAsync("lib", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryRecord?>(existing));
        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));
        var finalizer = NewFinalizer(chunks: chunks, libraries: libraries);

        await finalizer.RunAsync(NewJob(version: "v2"), NewProgress(), TestContext.Current.CancellationToken);

        Assert.Equal("v2", existing.CurrentVersion);
        Assert.Contains("v2", existing.AllVersions);
        Assert.Equal(2, existing.AllVersions.Count);
    }

    [Fact]
    public async Task RunAsyncDoesNotDoubleAppendVersionAlreadyInAllVersions()
    {
        var existing = new LibraryRecord
            {
                Id = "lib",
                Name = "lib",
                Hint = "lib",
                CurrentVersion = "v1",
                AllVersions = ["v1", "v2"]
            };
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetLibraryAsync("lib", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryRecord?>(existing));
        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));
        var finalizer = NewFinalizer(chunks: chunks, libraries: libraries);

        await finalizer.RunAsync(NewJob(version: "v2"), NewProgress(), TestContext.Current.CancellationToken);

        Assert.Equal(2, existing.AllVersions.Count);
    }

    [Fact]
    public async Task RunAsyncUpsertsVersionRecordWithEmbeddingProviderMetadata()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryRecord?>(null));
        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));
        var provider = StubProviderWith("provider-x", "model-x-fp16", 1024);
        var finalizer = NewFinalizer(chunks: chunks, libraries: libraries, provider: provider);

        await finalizer.RunAsync(NewJob(), NewProgress(pagesFetched: 11, chunksCompleted: 47),
                                 TestContext.Current.CancellationToken
                                );

        await libraries.Received(1)
                       .UpsertVersionAsync(Arg.Is<LibraryVersionRecord>(v => v.LibraryId == "lib"
                                                                          && v.Version == "v1"
                                                                          && v.PageCount == 11
                                                                          && v.ChunkCount == 47
                                                                          && v.EmbeddingProviderId == "provider-x"
                                                                          && v.EmbeddingModelName == "model-x-fp16"
                                                                          && v.EmbeddingDimensions == 1024),
                                           Arg.Any<CancellationToken>()
                                          );
    }

    [Fact]
    public async Task RunAsyncSetsSuspectWhenDetectorReturnsReasons()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryRecord?>(null));
        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));
        chunks.GetLanguageMixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyDictionary<string, double>>(
                  new Dictionary<string, double>())
                      );
        chunks.GetHostnameDistributionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyDictionary<string, int>>(
                  new Dictionary<string, int>())
                      );
        chunks.GetSampleTitlesAsync(Arg.Any<string>(),
                                    Arg.Any<string>(),
                                    Arg.Any<int>(),
                                    Arg.Any<CancellationToken>()
                                   )
              .Returns(Task.FromResult<IReadOnlyList<string>>([]));

        // PagesCompleted == 1 triggers SuspectReason.OnePager
        var finalizer = NewFinalizer(chunks: chunks, libraries: libraries);

        var progress = NewProgress();
        progress.PagesCompleted = 1;
        await finalizer.RunAsync(NewJob(), progress, TestContext.Current.CancellationToken);

        await libraries.Received(1)
                       .SetSuspectAsync("lib", "v1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await libraries.DidNotReceiveWithAnyArgs()
                       .ClearSuspectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsyncClearsSuspectWhenDetectorReturnsNoReasons()
    {
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryRecord?>(null));
        var chunks = Substitute.For<IChunkRepository>();
        chunks.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));
        chunks.GetLanguageMixAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyDictionary<string, double>>(
                  new Dictionary<string, double>())
                      );
        // Pretend there's plenty of host diversity so SingleHost doesn't trigger.
        chunks.GetHostnameDistributionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyDictionary<string, int>>(
                  new Dictionary<string, int> { ["a"] = 1, ["b"] = 1, ["c"] = 1 })
                      );
        chunks.GetSampleTitlesAsync(Arg.Any<string>(),
                                    Arg.Any<string>(),
                                    Arg.Any<int>(),
                                    Arg.Any<CancellationToken>()
                                   )
              .Returns(Task.FromResult<IReadOnlyList<string>>(["t1", "t2", "t3"]));
        var finalizer = NewFinalizer(chunks: chunks, libraries: libraries);

        // PagesCompleted > OnePagerThreshold, no other reasons.
        var progress = NewProgress();
        progress.PagesCompleted = 50;
        await finalizer.RunAsync(NewJob(), progress, TestContext.Current.CancellationToken);

        await libraries.Received(1)
                       .ClearSuspectAsync("lib", "v1", Arg.Any<CancellationToken>());
        await libraries.DidNotReceiveWithAnyArgs()
                       .SetSuspectAsync(Arg.Any<string>(),
                                        Arg.Any<string>(),
                                        Arg.Any<IReadOnlyList<string>>(),
                                        Arg.Any<CancellationToken>()
                                       );
    }
}
