// PackagingRoundTripTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Packaging;
using SaddleRAG.Tests.Packaging.Fixtures;

#endregion

namespace SaddleRAG.Tests.Packaging;

/// <summary>
///     Real-MongoDB round-trip test for the library import/export pipeline.
///     Seeds data via real repositories, exports to a temp bundle, drops the
///     seeded data, re-imports, then asserts byte-exact embedding fidelity
///     and matching counts. Tagged Category=Integration so this test runs
///     only in CI's integration-test lane and not the unit lane.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackagingRoundTripTests : IAsyncLifetime
{
    private string mDbName = string.Empty;
    private SaddleRagDbContext mContext = new SaddleRagDbContext(Options.Create(new SaddleRagDbSettings()));
    private string mTempBundlePath = string.Empty;

    public async ValueTask InitializeAsync()
    {
        mDbName = $"saddlerag-packaging-{Guid.NewGuid():N}";
        var settings = Options.Create(new SaddleRagDbSettings
                                          {
                                              ConnectionString = TestConnectionString,
                                              DatabaseName = mDbName
                                          });
        mContext = new SaddleRagDbContext(settings);
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

        mTempBundlePath = Path.Combine(Path.GetTempPath(),
                                        $"saddlerag-roundtrip-{Guid.NewGuid():N}.srlib.zip");
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDbName);
        if (File.Exists(mTempBundlePath))
            File.Delete(mTempBundlePath);
    }

    [Fact]
    public async Task FullRoundTripPreservesEverything()
    {
        const string LibraryId = "rt-test";
        const string Version = "1.0";
        const int PageCount = 3;
        const int ChunkCount = 5;
        const int Dim = 8;

        // ARRANGE: build fixture data.
        var library = PackagingFixtures.MakeLibrary(LibraryId, Version);

        // Override the Id to match the slash-delimited convention used by LibraryRepository.GetVersionAsync
        // so that the repo round-trips correctly (fixture uses a dash which is correct for unit tests).
        var version = PackagingFixtures.MakeVersion(LibraryId, Version, pageCount: PageCount, chunkCount: ChunkCount, dim: Dim)
                                       with { Id = $"{LibraryId}/{Version}" };
        var pages = PackagingFixtures.MakePages(LibraryId, Version, count: PageCount);
        var chunks = PackagingFixtures.MakeChunks(LibraryId, Version, count: ChunkCount, dim: Dim);

        // Seed via real repositories.
        var libRepo = new LibraryRepository(mContext);
        var pageRepo = new PageRepository(mContext);
        var chunkRepo = new ChunkRepository(mContext);
        var profileRepo = new LibraryProfileRepository(mContext);
        var indexRepo = new LibraryIndexRepository(mContext);
        var excludedRepo = new ExcludedSymbolsRepository(mContext);
        var diffRepo = new DiffRepository(mContext);
        var bm25Repo = new Bm25ShardRepository(mContext);
        var jobRepo = new JobRepository(mContext);

        await libRepo.UpsertLibraryAsync(library, TestContext.Current.CancellationToken);
        await libRepo.UpsertVersionAsync(version, TestContext.Current.CancellationToken);
        foreach (var page in pages)
            await pageRepo.UpsertPageAsync(page, TestContext.Current.CancellationToken);
        await chunkRepo.InsertChunksAsync(chunks, TestContext.Current.CancellationToken);

        // EXPORT to temp bundle.
        var exporter = new LibraryExporter(libRepo, profileRepo, indexRepo, excludedRepo,
                                           diffRepo, pageRepo, chunkRepo, bm25Repo);
        var exportResult = await exporter.ExportAsync(
            new ExportRequest
                {
                    LibraryId = LibraryId,
                    Versions = VersionFilter.Current,
                    OutputPath = mTempBundlePath
                },
            progress: null,
            ct: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(mTempBundlePath));
        Assert.Equal(ChunkCount, exportResult.TotalChunks);
        Assert.Equal(PageCount, exportResult.TotalPages);

        // DROP: use DeleteVersionAsync — it deletes the library row when last version is removed.
        await chunkRepo.DeleteChunksAsync(LibraryId, Version, TestContext.Current.CancellationToken);
        await pageRepo.DeleteAsync(LibraryId, Version, TestContext.Current.CancellationToken);
        await libRepo.DeleteVersionAsync(LibraryId, Version, TestContext.Current.CancellationToken);

        // Verify the drop happened.
        var droppedLib = await libRepo.GetLibraryAsync(LibraryId, TestContext.Current.CancellationToken);
        Assert.Null(droppedLib);

        // IMPORT: encoder matches the bundle (same providerId / modelName / dimensions).
        var matchingEmbeddingProvider = new RoundTripFakeEmbeddingProvider("onnx-local", "test-embed", Dim);

        var importer = new LibraryImporter(libRepo, jobRepo, matchingEmbeddingProvider,
                                           profileRepo, indexRepo, excludedRepo,
                                           diffRepo, pageRepo, chunkRepo,
                                           bm25Repo);

        var importResult = await importer.ImportAsync(
            new ImportRequest { BundlePath = mTempBundlePath },
            progress: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Contains(Version, importResult.VersionsImported);
        Assert.Empty(importResult.PartialFailures);
        Assert.Empty(importResult.PendingReembedJobIds);

        // ASSERT: library record round-tripped.
        var reLib = await libRepo.GetLibraryAsync(LibraryId, TestContext.Current.CancellationToken);
        Assert.NotNull(reLib);
        Assert.Equal(LibraryId, reLib.Id);
        Assert.Contains(Version, reLib.AllVersions);

        // ASSERT: version record round-tripped.
        var reVersion = await libRepo.GetVersionAsync(LibraryId, Version, TestContext.Current.CancellationToken);
        Assert.NotNull(reVersion);
        Assert.Equal(version.PageCount, reVersion.PageCount);
        Assert.Equal(version.ChunkCount, reVersion.ChunkCount);

        // ASSERT: pages round-tripped.
        var rePages = await pageRepo.GetPagesAsync(LibraryId, Version, TestContext.Current.CancellationToken);
        Assert.Equal(PageCount, rePages.Count);

        // ASSERT: chunks round-tripped with byte-exact embeddings.
        var reChunks = await chunkRepo.GetChunksAsync(LibraryId, Version, TestContext.Current.CancellationToken);
        Assert.Equal(ChunkCount, reChunks.Count);

        foreach (var reChunk in reChunks)
        {
            var orig = chunks.First(c => c.Id == reChunk.Id);
            Assert.NotNull(reChunk.Embedding);
            Assert.NotNull(orig.Embedding);
            Assert.Equal(orig.Embedding.Length, reChunk.Embedding.Length);
            for (int j = 0; j < orig.Embedding.Length; j++)
            {
                var origBits = BitConverter.SingleToInt32Bits(orig.Embedding[j]);
                var actualBits = BitConverter.SingleToInt32Bits(reChunk.Embedding[j]);
                Assert.Equal(origBits, actualBits);
            }
        }
    }

    private const string TestConnectionString = "mongodb://localhost:27017";
}

/// <summary>
///     Minimal <see cref="IEmbeddingProvider" /> for the round-trip integration
///     test. Supplies the identity fields so the importer recognises an
///     encoder-match and restores embeddings from the bundle verbatim.
///     <see cref="EmbedAsync" /> is never called on the encoder-match path
///     and throws to make unintended invocations visible.
/// </summary>
internal sealed class RoundTripFakeEmbeddingProvider : IEmbeddingProvider
{
    public RoundTripFakeEmbeddingProvider(string providerId, string modelName, int dimensions)
    {
        ProviderId = providerId;
        ModelName = modelName;
        Dimensions = dimensions;
    }

    public string ProviderId { get; }
    public string ModelName { get; }
    public int Dimensions { get; }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts,
                                      EmbedRole role = EmbedRole.Document,
                                      CancellationToken ct = default)
        => throw new NotSupportedException(
            "Round-trip test should not call EmbedAsync; the encoder matches by construction.");
}
