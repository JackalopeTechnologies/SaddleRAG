// RescrubServiceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Recon;
using SaddleRAG.Ingestion.Symbols;

#endregion

namespace SaddleRAG.Tests.Recon;

public sealed class RescrubServiceTests
{
    [Fact]
    public async Task RebuildsBm25AndIndexEvenWhenProfileMissingButStillReportsReconNeeded()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns((LibraryProfile?) null);
        var legacyChunk = MakeLegacyChunk("class Controller { void MoveLinear() { } }");
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([legacyChunk]);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                "lib",
                                                "1.0",
                                                new RescrubOptions(),
                                                ct: TestContext.Current.CancellationToken
                                               );

        Assert.True(result.ReconNeeded);
        Assert.True(result.IndexesBuilt);
        Assert.Equal(expected: 1, result.Processed);
        Assert.Equal(expected: 0, result.Changed);

        await bm25ShardRepo.Received(requiredNumberOfCalls: 1)
                           .ReplaceShardsAsync("lib",
                                               "1.0",
                                               Arg.Any<IReadOnlyList<Bm25Shard>>(),
                                               Arg.Any<CancellationToken>()
                                              );
        await indexRepo.Received(requiredNumberOfCalls: 1)
                       .UpsertAsync(Arg.Any<LibraryIndex>(), Arg.Any<CancellationToken>());

        // Symbol extraction must NOT run without a profile.
        await chunkRepo.DidNotReceive()
                       .UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoProfilePathPreservesExistingManifestParserAndClassifierVersionsButClearsProfileHash()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns((LibraryProfile?) null);
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([MakeLegacyChunk("class Controller { }")]);

        const int PriorParserVersion = 9;
        const string OrphanProfileHash = "orphan-hash";
        const string PriorClassifierVersion = "prior-classifier-v1";

        indexRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new LibraryIndex
                              {
                                  Id = "lib/1.0",
                                  LibraryId = "lib",
                                  Version = "1.0",
                                  Manifest = new LibraryManifest
                                                 {
                                                     LastParserVersion = PriorParserVersion,
                                                     LastProfileHash = OrphanProfileHash,
                                                     LastClassifierVersion = PriorClassifierVersion
                                                 }
                              }
                         );

        await service.RescrubAsync(chunkRepo,
                                   profileRepo,
                                   indexRepo,
                                   bm25ShardRepo,
                                   excludedRepo,
                                   libraryRepo,
                                   "lib",
                                   "1.0",
                                   new RescrubOptions(),
                                   ct: TestContext.Current.CancellationToken
                                  );

        await indexRepo.Received(requiredNumberOfCalls: 1)
                       .UpsertAsync(Arg.Is<LibraryIndex>(idx => idx.Manifest.LastParserVersion == PriorParserVersion &&
                                                                idx.Manifest.LastProfileHash == string.Empty &&
                                                                idx.Manifest.LastClassifierVersion ==
                                                                PriorClassifierVersion
                                                        ),
                                    Arg.Any<CancellationToken>()
                                   );
    }

    [Fact]
    public async Task NoProfilePathWritesZeroParserVersionWhenNoPriorIndexExists()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns((LibraryProfile?) null);
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([MakeLegacyChunk("class Controller { }")]);
        indexRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns((LibraryIndex?) null);

        await service.RescrubAsync(chunkRepo,
                                   profileRepo,
                                   indexRepo,
                                   bm25ShardRepo,
                                   excludedRepo,
                                   libraryRepo,
                                   "lib",
                                   "1.0",
                                   new RescrubOptions(),
                                   ct: TestContext.Current.CancellationToken
                                  );

        // LastParserVersion stays 0 (sentinel) because the symbol parser
        // is never run on the no-profile path — writing Current would
        // make downstream stale-detection lie.
        await indexRepo.Received(requiredNumberOfCalls: 1)
                       .UpsertAsync(Arg.Is<LibraryIndex>(idx => idx.Manifest.LastParserVersion == 0 &&
                                                                idx.Manifest.LastProfileHash == string.Empty
                                                        ),
                                    Arg.Any<CancellationToken>()
                                   );
    }

    [Fact]
    public async Task DryRunSkipsIndexWritesWhenProfileMissing()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns((LibraryProfile?) null);
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([MakeLegacyChunk("class Controller { }")]);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                "lib",
                                                "1.0",
                                                new RescrubOptions { DryRun = true },
                                                ct: TestContext.Current.CancellationToken
                                               );

        Assert.True(result.DryRun);
        Assert.False(result.IndexesBuilt);
        await bm25ShardRepo.DidNotReceive()
                           .ReplaceShardsAsync(Arg.Any<string>(),
                                               Arg.Any<string>(),
                                               Arg.Any<IReadOnlyList<Bm25Shard>>(),
                                               Arg.Any<CancellationToken>()
                                              );
        await indexRepo.DidNotReceive().UpsertAsync(Arg.Any<LibraryIndex>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DryRunDoesNotWriteChunks()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeProfile());

        var legacyChunk = MakeLegacyChunk("class Controller { }");
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([legacyChunk]);

        var options = new RescrubOptions { DryRun = true };
        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                "lib",
                                                "1.0",
                                                options,
                                                ct: TestContext.Current.CancellationToken
                                               );

        Assert.True(result.DryRun);
        Assert.Equal(expected: 1, result.Processed);
        Assert.True(result.Changed > 0);
        await chunkRepo.DidNotReceive()
                       .UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());
        await indexRepo.DidNotReceive().UpsertAsync(Arg.Any<LibraryIndex>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BumpsParserVersionAndPersistsChunks()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                   .Returns(MakeProfile());

        var legacyChunk = MakeLegacyChunk("class Controller { void MoveLinear() { } }");
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([legacyChunk]);

        var options = new RescrubOptions();
        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                "lib",
                                                "1.0",
                                                options,
                                                ct: TestContext.Current.CancellationToken
                                               );

        Assert.False(result.DryRun);
        Assert.Equal(expected: 1, result.Processed);
        Assert.True(result.Changed > 0);
        Assert.True(result.IndexesBuilt);

        await chunkRepo.Received(requiredNumberOfCalls: 1)
                       .UpsertChunksAsync(Arg.Is<IReadOnlyList<DocChunk>>(list => list.Count == 1 &&
                                                                              list[0].ParserVersion ==
                                                                              ParserVersionInfo.Current &&
                                                                              list[0].Symbols.Count > 0
                                                                         ),
                                          Arg.Any<CancellationToken>()
                                         );

        await indexRepo.Received(requiredNumberOfCalls: 1)
                       .UpsertAsync(Arg.Is<LibraryIndex>(idx => idx.Manifest.LastParserVersion ==
                                                                ParserVersionInfo.Current
                                                        ),
                                    Arg.Any<CancellationToken>()
                                   );
    }

    [Fact]
    public async Task IsIdempotentWhenChunksAreAlreadyCurrent()
    {
        var classifier = MakeClassifier();
        var service = new RescrubService(new SymbolExtractor(), classifier, NullLogger<RescrubService>.Instance);
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        var profile = MakeProfile();
        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(profile);

        var alreadyCurrent = MakeCurrentChunkFromContent("class Controller { void MoveLinear() { } }", profile);

        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([alreadyCurrent]);

        // Existing index whose manifest matches the current parser/profile/classifier exactly,
        // so auto-detect skips reclassification and the rescrub finds no changes.
        indexRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns(new LibraryIndex
                              {
                                  Id = "lib/1.0",
                                  LibraryId = "lib",
                                  Version = "1.0",
                                  Manifest = new LibraryManifest
                                                 {
                                                     LastParserVersion = ParserVersionInfo.Current,
                                                     LastProfileHash = LibraryProfileService.ComputeHash(profile),
                                                     LastClassifierVersion = classifier.GetCurrentVersion()
                                                 }
                              }
                         );

        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                "lib",
                                                "1.0",
                                                new RescrubOptions(),
                                                ct: TestContext.Current.CancellationToken
                                               );

        Assert.Equal(expected: 1, result.Processed);
        Assert.Equal(expected: 0, result.Changed);
        Assert.False(result.DidReclassify);
        await chunkRepo.DidNotReceive()
                       .UpsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>());
    }

    private static RescrubService MakeService()
    {
        var classifier = MakeClassifier();
        var extractor = new SymbolExtractor();
        var service = new RescrubService(extractor, classifier, NullLogger<RescrubService>.Instance);
        return service;
    }

    private static ILlmClassifier MakeClassifier()
    {
        var ollamaSettings = new OllamaSettings();
        ollamaSettings.ClassificationModels.Add(new OllamaModelEntry { Name = "test-classifier:latest" });
        var settings = Options.Create(ollamaSettings);
        var result = new OllamaLlmClassifier(settings,
                                       NullLogger<OllamaLlmClassifier>.Instance
                                      );
        return result;
    }

    private static LibraryProfile MakeProfile()
    {
        var result = new LibraryProfile
                         {
                             Id = "lib/1.0",
                             LibraryId = "lib",
                             Version = "1.0",
                             Source = "test",
                             Languages = ["C#"],
                             Casing = new CasingConventions { Types = "PascalCase" }
                         };
        return result;
    }

    private static DocChunk MakeLegacyChunk(string content) =>
        new DocChunk
            {
                Id = "lib/1.0/abc/0",
                LibraryId = "lib",
                Version = "1.0",
                PageUrl = "https://example.com/page",
                PageTitle = "Page",
                Category = DocCategory.ApiReference,
                Content = content,
                ParserVersion = 1
            };

    private static DocChunk MakeCurrentChunkFromContent(string content, LibraryProfile profile)
    {
        var extractor = new SymbolExtractor();
        var extracted = extractor.Extract(content, profile);
        var result = new DocChunk
                         {
                             Id = "lib/1.0/abc/0",
                             LibraryId = "lib",
                             Version = "1.0",
                             PageUrl = "https://example.com/page",
                             PageTitle = "Page",
                             Category = DocCategory.ApiReference,
                             Content = content,
                             Symbols = extracted.Symbols,
                             QualifiedName = extracted.PrimaryQualifiedName,
                             ParserVersion = ParserVersionInfo.Current
                         };
        return result;
    }

    [Fact]
    public async Task PersistsRejectionsToExcludedRepository()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(MakeProfile());
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([MakeLegacyChunk("The axis homes when MoveLinear runs.")]);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                "lib",
                                                "1.0",
                                                new RescrubOptions(),
                                                ct: TestContext.Current.CancellationToken
                                               );

        await excludedRepo.Received(requiredNumberOfCalls: 1).DeleteAsync("lib", "1.0", Arg.Any<CancellationToken>());
        await excludedRepo.Received(requiredNumberOfCalls: 1)
                          .UpsertManyAsync(Arg.Any<IEnumerable<ExcludedSymbol>>(), Arg.Any<CancellationToken>());
        Assert.True(result.ExcludedCount > 0);
    }

    [Fact]
    public async Task DryRunDoesNotPersistRejections()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(MakeProfile());
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([MakeLegacyChunk("The axis homes when MoveLinear runs.")]);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                "lib",
                                                "1.0",
                                                new RescrubOptions { DryRun = true },
                                                ct: TestContext.Current.CancellationToken
                                               );

        await excludedRepo.DidNotReceive()
                          .DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await excludedRepo.DidNotReceive()
                          .UpsertManyAsync(Arg.Any<IEnumerable<ExcludedSymbol>>(), Arg.Any<CancellationToken>());
        Assert.True(result.ExcludedCount > 0);
    }

    [Fact]
    public async Task EmitsHintsWhenRatioAndCountThresholdsMet()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(MakeProfile());
        var noiseChunks = Enumerable.Range(start: 0, count: 30)
                                    .Select(i => MakeLegacyChunk($"alpha{i} beta{i} gamma{i} delta{i} epsilon{i}."))
                                    .ToArray();
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(noiseChunks);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                "lib",
                                                "1.0",
                                                new RescrubOptions(),
                                                ct: TestContext.Current.CancellationToken
                                               );

        Assert.True(result.ExcludedCount >= 20);
        Assert.NotEmpty(result.Hints);
        Assert.Contains(result.Hints, h => h.Contains("list_excluded_symbols"));
    }

    [Fact]
    public async Task SuppressesHintsBelowAbsoluteFloor()
    {
        var service = MakeService();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var bm25ShardRepo = Substitute.For<IBm25ShardRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var libraryRepo = Substitute.For<ILibraryRepository>();

        profileRepo.GetAsync("lib", "1.0", Arg.Any<CancellationToken>()).Returns(MakeProfile());
        chunkRepo.GetChunksAsync("lib", "1.0", Arg.Any<CancellationToken>())
                 .Returns([MakeLegacyChunk("The axis homes.")]);

        var result = await service.RescrubAsync(chunkRepo,
                                                profileRepo,
                                                indexRepo,
                                                bm25ShardRepo,
                                                excludedRepo,
                                                libraryRepo,
                                                "lib",
                                                "1.0",
                                                new RescrubOptions(),
                                                ct: TestContext.Current.CancellationToken
                                               );

        Assert.Empty(result.Hints);
    }
}
