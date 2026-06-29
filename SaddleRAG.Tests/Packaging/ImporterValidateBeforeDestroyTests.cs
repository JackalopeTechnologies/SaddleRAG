// ImporterValidateBeforeDestroyTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
///     Integration regression test: validate-before-destroy guarantee on
///     <see cref="LibraryImporter.ImportAsync" />.
///
///     The test proves that a corrupt bundle (where a blob's bytes no longer
///     match the SHA-256 recorded in the manifest) is rejected BEFORE any
///     destructive step runs.  That is, <c>ValidateAllBlobs</c> fires and
///     throws <see cref="InvalidOperationException" /> before
///     <c>PurgeVersionAsync</c> can delete the caller's pre-existing data.
///
///     REPRO RESULT (2026-06-29): PASS.
///     The guarantee already held in the code as written: <c>ValidateAllBlobs</c>
///     is called at line 127 of LibraryImporter.cs, well before the overwrite
///     purge block at line 177.  No production-code change was required.
///
///     ONEDRIVE CAVEAT: the original OneDrive incident involved a cloud-only
///     placeholder file that failed to open before the zip was even read
///     (IOException: Access to the cloud file is denied).  That failure path
///     is a distinct scenario — the bundle never makes it past the
///     <c>new ZipBundleReader(request.BundlePath)</c> constructor, so the
///     deletion that was observed there must have come from elsewhere
///     (e.g. a separate manual operation).  A dedicated repro for the
///     cloud-placeholder open-failure is left as future work.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ImporterValidateBeforeDestroyTests : IAsyncLifetime
{
    public ImporterValidateBeforeDestroyTests()
    {
        mDbName = $"saddlerag-test-import-guard-{Guid.NewGuid():N}";
        var settings = Options.Create(new SaddleRagDbSettings
                                          {
                                              ConnectionString = TestConnectionString,
                                              DatabaseName = mDbName
                                          });
        mContext = new SaddleRagDbContext(settings);
    }

    private readonly string mDbName;
    private readonly SaddleRagDbContext mContext;
    private string mValidBundlePath = string.Empty;
    private string mCorruptBundlePath = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

        mValidBundlePath = Path.Combine(Path.GetTempPath(),
                                         $"saddlerag-import-guard-valid-{Guid.NewGuid():N}.srlib.zip");
        mCorruptBundlePath = Path.Combine(Path.GetTempPath(),
                                           $"saddlerag-import-guard-corrupt-{Guid.NewGuid():N}.srlib.zip");
    }

    public async ValueTask DisposeAsync()
    {
        await mContext.Database.Client.DropDatabaseAsync(mDbName);
        if (File.Exists(mValidBundlePath))
            File.Delete(mValidBundlePath);
        if (File.Exists(mCorruptBundlePath))
            File.Delete(mCorruptBundlePath);
    }

    /// <summary>
    ///     Seeds library L/version V with real data, builds a valid bundle,
    ///     corrupts one blob's bytes inside a copy of the zip (so its SHA-256
    ///     no longer matches the manifest), then calls
    ///     <see cref="LibraryImporter.ImportAsync" /> with
    ///     <c>overwrite=true</c>.
    ///
    ///     Assertions:
    ///     (a) <see cref="InvalidOperationException" /> is thrown (integrity
    ///         check fires before any destructive step).
    ///     (b) The pre-existing library row, version record, and chunk data
    ///         are all still present after the failed import attempt.
    /// </summary>
    [Fact]
    public async Task FailedBundleValidationLeavesExistingDataIntact()
    {
        const string LibraryId = "import-guard-lib";
        const string Version = "1.0";
        const int PageCount = 2;
        const int ChunkCount = 3;
        const int Dim = PackagingFixtures.DefaultDim;

        var ct = TestContext.Current.CancellationToken;

        // ── ARRANGE: seed real data via repositories ──────────────────────
        var library = PackagingFixtures.MakeLibrary(LibraryId, Version);
        var version = PackagingFixtures.MakeVersion(LibraryId, Version,
                                                     pageCount: PageCount,
                                                     chunkCount: ChunkCount,
                                                     dim: Dim)
                                       with { Id = $"{LibraryId}/{Version}" };
        var pages = PackagingFixtures.MakePages(LibraryId, Version, count: PageCount);
        var chunks = PackagingFixtures.MakeChunks(LibraryId, Version, count: ChunkCount, dim: Dim);

        var libRepo = new LibraryRepository(mContext);
        var pageRepo = new PageRepository(mContext);
        var chunkRepo = new ChunkRepository(mContext);
        var profileRepo = new LibraryProfileRepository(mContext);
        var indexRepo = new LibraryIndexRepository(mContext);
        var excludedRepo = new ExcludedSymbolsRepository(mContext);
        var diffRepo = new DiffRepository(mContext);
        var bm25Repo = new Bm25ShardRepository(mContext);
        var jobRepo = new JobRepository(mContext);

        await libRepo.UpsertLibraryAsync(library, ct);
        await libRepo.UpsertVersionAsync(version, ct);
        foreach (var page in pages)
            await pageRepo.UpsertPageAsync(page, ct);
        await chunkRepo.InsertChunksAsync(chunks, ct);

        // ── EXPORT: build a valid bundle of L/V ──────────────────────────
        var exporter = new LibraryExporter(libRepo, profileRepo, indexRepo, excludedRepo,
                                           diffRepo, pageRepo, chunkRepo, bm25Repo);
        await exporter.ExportAsync(
            new ExportRequest
                {
                    LibraryId = LibraryId,
                    Versions = VersionFilter.Current,
                    OutputPath = mValidBundlePath
                },
            progress: null,
            ct: ct);

        // ── CORRUPT: rebuild the zip with one blob's bytes replaced ───────
        // Copy every entry from the valid bundle verbatim, except pages.jsonl
        // whose bytes are replaced with garbage.  The manifest is left
        // unchanged so its SHA-256 claim no longer matches the actual bytes.
        var pagesEntryName = BundlePaths.VersionFilePath(Version, BundlePaths.PagesFile);
        BuildCorruptBundle(mValidBundlePath, mCorruptBundlePath, pagesEntryName);

        // ── ACT: import the corrupt bundle with overwrite=true ────────────
        var embeddingProvider = new ImportGuardFakeEmbeddingProvider("onnx-local", "test-embed", Dim);
        var importer = new LibraryImporter(libRepo, jobRepo, embeddingProvider,
                                           profileRepo, indexRepo, excludedRepo,
                                           diffRepo, pageRepo, chunkRepo, bm25Repo);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(
                new ImportRequest { BundlePath = mCorruptBundlePath, Overwrite = true },
                progress: null,
                ct: ct));

        // ── ASSERT: pre-existing data is still intact ─────────────────────
        var reLib = await libRepo.GetLibraryAsync(LibraryId, ct);
        Assert.NotNull(reLib);

        var reVersion = await libRepo.GetVersionAsync(LibraryId, Version, ct);
        Assert.NotNull(reVersion);

        var chunkCount = await chunkRepo.GetChunkCountAsync(LibraryId, Version, ct);
        Assert.True(chunkCount > 0, $"Expected chunks > 0 but got {chunkCount} — overwrite purge ran before validation.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     Copies every entry from <paramref name="sourcePath" /> into
    ///     <paramref name="destPath" />, but replaces the entry named
    ///     <paramref name="entryToCorrupt" /> with a single garbage byte so
    ///     its SHA-256 no longer matches the value stored in the manifest.
    ///     The manifest entry itself is copied verbatim, preserving the
    ///     original (now-wrong) hash claim.
    /// </summary>
    private static void BuildCorruptBundle(string sourcePath, string destPath, string entryToCorrupt)
    {
        using var sourceArchive = ZipFile.OpenRead(sourcePath);
        using var destFs = File.Create(destPath);
        using var destArchive = new ZipArchive(destFs, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var entry in sourceArchive.Entries)
        {
            var destEntry = destArchive.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
            using var src = entry.Open();
            using var dst = destEntry.Open();

            bool isCorruptTarget = string.Equals(entry.FullName,
                                                  entryToCorrupt,
                                                  StringComparison.Ordinal);
            if (isCorruptTarget)
            {
                // Write a single byte that differs from the real content.
                dst.WriteByte(0xFF);
            }
            else
            {
                src.CopyTo(dst);
            }
        }
    }

    private const string TestConnectionString = "mongodb://localhost:27017";

    /// <summary>
    ///     Minimal <see cref="IEmbeddingProvider" /> for the import-guard
    ///     integration test.  Encoder fields match the bundle so the importer
    ///     recognises an encoder-match; <see cref="EmbedAsync" /> is never
    ///     called and throws to make any accidental invocation visible.
    /// </summary>
    private sealed class ImportGuardFakeEmbeddingProvider : IEmbeddingProvider
    {
        public ImportGuardFakeEmbeddingProvider(string providerId, string modelName, int dimensions)
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
                "Import-guard test should not call EmbedAsync; validation fails before embedding is needed.");
    }
}
