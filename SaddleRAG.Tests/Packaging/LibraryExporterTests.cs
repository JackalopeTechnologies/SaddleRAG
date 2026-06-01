// LibraryExporterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Packaging;
using SaddleRAG.Tests.Packaging.Fixtures;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class LibraryExporterTests
{
    private static (LibraryExporter exporter,
                    ILibraryRepository libRepo,
                    ILibraryProfileRepository profileRepo,
                    ILibraryIndexRepository indexRepo,
                    IExcludedSymbolsRepository excludedRepo,
                    IDiffRepository diffRepo,
                    IPageRepository pageRepo,
                    string outDir) BuildExporter(LibraryRecord library,
                                                 LibraryVersionRecord? versionRecord = null,
                                                 IReadOnlyList<PageRecord>? pages = null,
                                                 IReadOnlyList<DocChunk>? chunks = null,
                                                 IBm25ShardRepository? bm25Repo = null)
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync(library.Id, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(library));

        if (versionRecord is not null)
            libRepo.GetVersionAsync(library.Id, versionRecord.Version, Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryVersionRecord?>(versionRecord));

        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        profileRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryProfile?>(null));

        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        indexRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryIndex?>(null));

        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        excludedRepo.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SymbolRejectionReason?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ExcludedSymbol>>([]));

        var diffRepo = Substitute.For<IDiffRepository>();
        diffRepo.GetDiffAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<VersionDiffRecord?>(null));

        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>(pages ?? []));

        var chunkRepo = Substitute.For<IChunkRepository>();
        chunkRepo.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<DocChunk>>(chunks ?? []));

        var resolvedBm25Repo = bm25Repo ?? BuildEmptyBm25Repo();
        var exporter = new LibraryExporter(libRepo, profileRepo, indexRepo, excludedRepo, diffRepo, pageRepo, chunkRepo, resolvedBm25Repo);
        var outDir = Path.Combine(Path.GetTempPath(), "saddlerag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        return (exporter, libRepo, profileRepo, indexRepo, excludedRepo, diffRepo, pageRepo, outDir);
    }

    private static IBm25ShardRepository BuildEmptyBm25Repo()
    {
        var repo = Substitute.For<IBm25ShardRepository>();
        repo.GetAllShardsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bm25Shard>>([]));
        return repo;
    }

    [Fact]
    public async Task EmitsManifestAndLibraryJson()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var versionRecord = PackagingFixtures.MakeVersion("foo", "1.0");
        var (exporter, _, _, _, _, _, _, outDir) = BuildExporter(library, versionRecord);
        var outPath = Path.Combine(outDir, "foo-1.0.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.Current,
                              OutputPath = outPath
                          };

        var result = await exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(outPath));
        Assert.True(result.BytesWritten > 0);

        using var archive = ZipFile.OpenRead(outPath);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        var libJsonEntry = archive.GetEntry("library.json");
        Assert.NotNull(libJsonEntry);

        using var libStream = libJsonEntry.Open();
        var lib = JsonSerializer.Deserialize<LibraryRecord>(libStream);
        Assert.NotNull(lib);
        Assert.Equal("foo", lib.Id);

        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream);
        Assert.NotNull(manifest);
        Assert.NotEmpty(manifest.Blobs);
        Assert.True(manifest.Blobs.ContainsKey("library.json"), "manifest.Blobs must contain library.json");

        var libJsonEntry2 = archive.GetEntry("library.json");
        Assert.NotNull(libJsonEntry2);
        byte[] actualBytes;
        using (var s = libJsonEntry2.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            actualBytes = ms.ToArray();
        }
        var actualSha256 = Convert.ToHexString(SHA256.HashData(actualBytes)).ToLowerInvariant();
        Assert.Equal(actualSha256, manifest.Blobs["library.json"].Sha256);
    }

    [Fact]
    public async Task NoOutputFileIfRequestedVersionMissing()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var (exporter, _, _, _, _, _, _, outDir) = BuildExporter(library);
        var outPath = Path.Combine(outDir, "foo-9.9.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.Parse(new[] { "9.9" }),
                              OutputPath = outPath
                          };

        await Assert.ThrowsAsync<ArgumentException>(() => exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public async Task ExportThrowsAndLeavesNoFileWhenCancelled()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
               .Returns(ci =>
               {
                   var ct = ci.Arg<CancellationToken>();
                   ct.ThrowIfCancellationRequested();
                   return Task.FromResult<LibraryRecord?>(library);
               });

        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var diffRepo = Substitute.For<IDiffRepository>();
        var pageRepo = Substitute.For<IPageRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        var bm25Repo = BuildEmptyBm25Repo();

        var exporter = new LibraryExporter(libRepo, profileRepo, indexRepo, excludedRepo, diffRepo, pageRepo, chunkRepo, bm25Repo);
        var outDir = Path.Combine(Path.GetTempPath(), "saddlerag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "foo-1.0.srlib.zip");
        var tempPath = outPath + ".tmp";

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.Current,
                              OutputPath = outPath
                          };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => exporter.ExportAsync(request, progress: null, ct: cts.Token));

        Assert.False(File.Exists(outPath));
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public async Task EmitsPerVersionMetadataFiles()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var versionRecord = PackagingFixtures.MakeVersion("foo", "1.0");
        var (exporter, _, _, _, _, _, _, outDir) = BuildExporter(library, versionRecord);
        var outPath = Path.Combine(outDir, "foo-1.0-meta.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.Current,
                              OutputPath = outPath
                          };

        await exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken);

        using var archive = ZipFile.OpenRead(outPath);

        var versionEntry = archive.GetEntry("versions/1.0/version.json");
        Assert.NotNull(versionEntry);

        var excludedEntry = archive.GetEntry("versions/1.0/excludedSymbols.jsonl");
        Assert.NotNull(excludedEntry);

        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream);
        Assert.NotNull(manifest);

        Assert.Single(manifest.Versions);
        var v = manifest.Versions[0];
        Assert.Equal("1.0", v.Version);
        Assert.NotEmpty(v.Blobs);
        Assert.True(v.Blobs.ContainsKey("versions/1.0/version.json"),
                    "BundleVersionEntry.Blobs must contain versions/1.0/version.json");
    }

    [Fact]
    public async Task EmitsTwoVersionsWithIsolatedBlobDictionaries()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.1", "1.0", "1.1");
        var versionRecord10 = PackagingFixtures.MakeVersion("foo", "1.0");
        var versionRecord11 = PackagingFixtures.MakeVersion("foo", "1.1");

        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync(library.Id, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(library));
        libRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryVersionRecord?>(versionRecord10));
        libRepo.GetVersionAsync("foo", "1.1", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryVersionRecord?>(versionRecord11));

        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        profileRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryProfile?>(null));

        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        indexRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryIndex?>(null));

        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        excludedRepo.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SymbolRejectionReason?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ExcludedSymbol>>([]));

        var diffRepo = Substitute.For<IDiffRepository>();
        diffRepo.GetDiffAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<VersionDiffRecord?>(null));

        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>([]));

        var chunkRepo = Substitute.For<IChunkRepository>();
        chunkRepo.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));

        var bm25RepoMulti = BuildEmptyBm25Repo();
        var exporter = new LibraryExporter(libRepo, profileRepo, indexRepo, excludedRepo, diffRepo, pageRepo, chunkRepo, bm25RepoMulti);
        var outDir = Path.Combine(Path.GetTempPath(), "saddlerag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "foo-all.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.All,
                              OutputPath = outPath
                          };

        await exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken);

        using var archive = ZipFile.OpenRead(outPath);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream);
        Assert.NotNull(manifest);

        Assert.Equal(2, manifest.Versions.Count);

        var entry10 = manifest.Versions.Single(v => v.Version == "1.0");
        var entry11 = manifest.Versions.Single(v => v.Version == "1.1");

        Assert.All(entry10.Blobs.Keys, key => Assert.StartsWith("versions/1.0/", key));
        Assert.All(entry11.Blobs.Keys, key => Assert.StartsWith("versions/1.1/", key));

        var overlap = entry10.Blobs.Keys.Intersect(entry11.Blobs.Keys).ToList();
        Assert.Empty(overlap);
    }

    [Fact]
    public async Task EmitsAllOptionalMetadataFilesWhenRepositoriesReturnContent()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var versionRecord = PackagingFixtures.MakeVersion("foo", "1.0") with { PreviousVersion = "0.9" };
        var profile = PackagingFixtures.MakeProfile("foo", "1.0");
        var index = PackagingFixtures.MakeIndex("foo", "1.0");
        var diff = PackagingFixtures.MakeVersionDiff("foo", "0.9", "1.0");

        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync(library.Id, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(library));
        libRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryVersionRecord?>(versionRecord));

        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        profileRepo.GetAsync("foo", "1.0", Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryProfile?>(profile));

        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        indexRepo.GetAsync("foo", "1.0", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryIndex?>(index));

        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        excludedRepo.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SymbolRejectionReason?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ExcludedSymbol>>([]));

        var diffRepo = Substitute.For<IDiffRepository>();
        diffRepo.GetDiffAsync("foo", "0.9", "1.0", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<VersionDiffRecord?>(diff));

        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>([]));

        var chunkRepo = Substitute.For<IChunkRepository>();
        chunkRepo.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));

        var bm25RepoFull = BuildEmptyBm25Repo();
        var exporter = new LibraryExporter(libRepo, profileRepo, indexRepo, excludedRepo, diffRepo, pageRepo, chunkRepo, bm25RepoFull);
        var outDir = Path.Combine(Path.GetTempPath(), "saddlerag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "foo-1.0-full.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.Current,
                              OutputPath = outPath
                          };

        await exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken);

        using var archive = ZipFile.OpenRead(outPath);

        Assert.NotNull(archive.GetEntry("versions/1.0/version.json"));
        Assert.NotNull(archive.GetEntry("versions/1.0/profile.json"));
        Assert.NotNull(archive.GetEntry("versions/1.0/index.json"));
        Assert.NotNull(archive.GetEntry("versions/1.0/versionDiff.json"));
        Assert.NotNull(archive.GetEntry("versions/1.0/excludedSymbols.jsonl"));

        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream);
        Assert.NotNull(manifest);

        Assert.Single(manifest.Versions);
        var v = manifest.Versions[0];

        Assert.True(v.Blobs.ContainsKey("versions/1.0/version.json"),
                    "Blobs must contain versions/1.0/version.json");
        Assert.True(v.Blobs.ContainsKey("versions/1.0/profile.json"),
                    "Blobs must contain versions/1.0/profile.json");
        Assert.True(v.Blobs.ContainsKey("versions/1.0/index.json"),
                    "Blobs must contain versions/1.0/index.json");
        Assert.True(v.Blobs.ContainsKey("versions/1.0/versionDiff.json"),
                    "Blobs must contain versions/1.0/versionDiff.json");
        Assert.True(v.Blobs.ContainsKey("versions/1.0/excludedSymbols.jsonl"),
                    "Blobs must contain versions/1.0/excludedSymbols.jsonl");
    }

    [Fact]
    public async Task EmitsPagesJsonl()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var versionRecord = PackagingFixtures.MakeVersion("foo", "1.0", pageCount: 3);
        var pages = PackagingFixtures.MakePages("foo", "1.0", count: 3);
        var (exporter, _, _, _, _, _, _, outDir) = BuildExporter(library, versionRecord, pages);
        var outPath = Path.Combine(outDir, "foo-1.0-pages.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.Current,
                              OutputPath = outPath
                          };

        await exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken);

        using var archive = ZipFile.OpenRead(outPath);

        var pagesEntry = archive.GetEntry("versions/1.0/pages.jsonl");
        Assert.NotNull(pagesEntry);

        using var pagesStream = pagesEntry.Open();
        var reader = new SaddleRAG.Packaging.Internal.JsonlReader<PageRecord>(pagesStream);
        var roundTripped = new List<PageRecord>();
        await foreach (var page in reader.ReadAllAsync(TestContext.Current.CancellationToken))
            roundTripped.Add(page);

        Assert.Equal(3, roundTripped.Count);
        Assert.All(roundTripped, p => Assert.StartsWith("https://example.test/p", p.Url));

        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream);
        Assert.NotNull(manifest);

        Assert.Single(manifest.Versions);
        Assert.True(manifest.Versions[0].Blobs.ContainsKey("versions/1.0/pages.jsonl"),
                    "Blobs must contain versions/1.0/pages.jsonl");
    }

    [Fact]
    public async Task EmitsChunksAndEmbeddingsInLockstep()
    {
        const int Dim = 8;
        const int Count = 4;

        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var versionRecord = PackagingFixtures.MakeVersion("foo", "1.0", chunkCount: Count, dim: Dim);
        var chunks = PackagingFixtures.MakeChunks("foo", "1.0", count: Count, dim: Dim);
        var (exporter, _, _, _, _, _, _, outDir) = BuildExporter(library, versionRecord, chunks: chunks);
        var outPath = Path.Combine(outDir, "foo-1.0-chunks.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.Current,
                              OutputPath = outPath
                          };

        await exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken);

        using var archive = ZipFile.OpenRead(outPath);

        var chunksEntry = archive.GetEntry("versions/1.0/chunks.jsonl");
        Assert.NotNull(chunksEntry);

        using var chunksStream = chunksEntry.Open();
        var reader = new SaddleRAG.Packaging.Internal.JsonlReader<DocChunk>(chunksStream);
        var roundTripped = new List<DocChunk>();
        await foreach (var chunk in reader.ReadAllAsync(TestContext.Current.CancellationToken))
            roundTripped.Add(chunk);

        Assert.Equal(Count, roundTripped.Count);
        Assert.All(roundTripped, c => Assert.Null(c.Embedding));

        var embedEntry = archive.GetEntry("versions/1.0/chunks.embeddings.f32");
        Assert.NotNull(embedEntry);

        using var embedStream = embedEntry.Open();
        using var embedMs = new MemoryStream();
        embedStream.CopyTo(embedMs);
        var embedBytes = embedMs.ToArray();
        Assert.Equal(Count * Dim * sizeof(float), embedBytes.Length);

        using var readBack = new MemoryStream(embedBytes);
        var embedReader = new SaddleRAG.Packaging.Internal.EmbeddingBlobReader(readBack, Dim);
        for (int i = 0; i < Count; i++)
        {
            var vector = await embedReader.ReadAsync(TestContext.Current.CancellationToken);
            var expected = chunks[i].Embedding ?? throw new InvalidOperationException("Fixture chunk has null embedding");
            Assert.Equal(Dim, vector.Length);
            for (int j = 0; j < Dim; j++)
                Assert.Equal(BitConverter.SingleToInt32Bits(expected[j]),
                             BitConverter.SingleToInt32Bits(vector[j]));
        }

        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream);
        Assert.NotNull(manifest);

        Assert.Single(manifest.Versions);
        var v = manifest.Versions[0];
        Assert.True(v.Blobs.ContainsKey("versions/1.0/chunks.jsonl"),
                    "Blobs must contain versions/1.0/chunks.jsonl");
        Assert.True(v.Blobs.ContainsKey("versions/1.0/chunks.embeddings.f32"),
                    "Blobs must contain versions/1.0/chunks.embeddings.f32");
    }

    [Fact]
    public async Task EmitsBm25ShardsWithoutGridFsWhenNoSpillover()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var versionRecord = PackagingFixtures.MakeVersion("foo", "1.0");
        var shard = PackagingFixtures.MakeBm25Shard("foo", "1.0");

        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        bm25Repo.GetAllShardsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Bm25Shard>>([shard]));

        var (exporter, _, _, _, _, _, _, outDir) = BuildExporter(library, versionRecord, bm25Repo: bm25Repo);
        var outPath = Path.Combine(outDir, "foo-1.0-bm25-inline.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.Current,
                              OutputPath = outPath
                          };

        await exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken);

        using var archive = ZipFile.OpenRead(outPath);

        var shardsEntry = archive.GetEntry("versions/1.0/bm25/shards.jsonl");
        Assert.NotNull(shardsEntry);

        var gridFsEntries = archive.Entries
                                   .Where(e => e.FullName.StartsWith("versions/1.0/bm25/gridfs/", StringComparison.Ordinal))
                                   .ToList();
        Assert.Empty(gridFsEntries);

        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream);
        Assert.NotNull(manifest);

        Assert.Single(manifest.Versions);
        Assert.False(manifest.Versions[0].Bm25HasGridFs,
                     "Bm25HasGridFs must be false when no spill occurred");
    }

    [Fact]
    public async Task EmitsBm25GridFsBlobWhenShardSpilled()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var versionRecord = PackagingFixtures.MakeVersion("foo", "1.0");
        var shard = PackagingFixtures.MakeBm25Shard("foo", "1.0",
                                                     inlineTerms: new Dictionary<string, IReadOnlyList<Bm25Posting>>(),
                                                     externalTerms: new Dictionary<string, string>(),
                                                     shardGridFsRef: "abc123");

        var blobBytes = new byte[] { 1, 2, 3, 4, 5 };

        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        bm25Repo.GetAllShardsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Bm25Shard>>([shard]));
        bm25Repo.OpenGridFsBlobAsync("abc123", Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Stream>(new MemoryStream(blobBytes)));

        var (exporter, _, _, _, _, _, _, outDir) = BuildExporter(library, versionRecord, bm25Repo: bm25Repo);
        var outPath = Path.Combine(outDir, "foo-1.0-bm25-gridfs.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.Current,
                              OutputPath = outPath
                          };

        await exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken);

        using var archive = ZipFile.OpenRead(outPath);

        var blobEntry = archive.GetEntry("versions/1.0/bm25/gridfs/abc123.bin");
        Assert.NotNull(blobEntry);

        using var blobStream = blobEntry.Open();
        using var blobMs = new MemoryStream();
        blobStream.CopyTo(blobMs);
        Assert.Equal(blobBytes, blobMs.ToArray());

        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<BundleManifest>(manifestStream);
        Assert.NotNull(manifest);

        Assert.Single(manifest.Versions);
        Assert.True(manifest.Versions[0].Bm25HasGridFs,
                    "Bm25HasGridFs must be true when a spill blob was bundled");
    }

    [Fact]
    public async Task RefusesExportWhenVersionsHaveDifferentEncoders()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0", "1.0", "2.0");

        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync(library.Id, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(library));
        libRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryVersionRecord?>(PackagingFixtures.MakeVersion("foo", "1.0", modelName: "model-A")));
        libRepo.GetVersionAsync("foo", "2.0", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryVersionRecord?>(PackagingFixtures.MakeVersion("foo", "2.0", modelName: "model-B")));

        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        profileRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryProfile?>(null));

        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        indexRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryIndex?>(null));

        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        excludedRepo.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SymbolRejectionReason?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ExcludedSymbol>>([]));

        var diffRepo = Substitute.For<IDiffRepository>();
        diffRepo.GetDiffAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<VersionDiffRecord?>(null));

        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>([]));

        var chunkRepo = Substitute.For<IChunkRepository>();
        chunkRepo.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));

        var exporter = new LibraryExporter(libRepo, profileRepo, indexRepo, excludedRepo, diffRepo, pageRepo, chunkRepo, BuildEmptyBm25Repo());
        var outDir = Path.Combine(Path.GetTempPath(), "saddlerag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "foo-all.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.All,
                              OutputPath = outPath
                          };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken));

        Assert.Contains("multiple embedding encoders", ex.Message);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public async Task ReturnsTotalPagesAndChunksAcrossVersions()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.1", "1.0", "1.1");
        var versionRecord10 = PackagingFixtures.MakeVersion("foo", "1.0", pageCount: 3, chunkCount: 10);
        var versionRecord11 = PackagingFixtures.MakeVersion("foo", "1.1", pageCount: 5, chunkCount: 20);

        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync(library.Id, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryRecord?>(library));
        libRepo.GetVersionAsync("foo", "1.0", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryVersionRecord?>(versionRecord10));
        libRepo.GetVersionAsync("foo", "1.1", Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<LibraryVersionRecord?>(versionRecord11));

        var profileRepo = Substitute.For<ILibraryProfileRepository>();
        profileRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult<LibraryProfile?>(null));

        var indexRepo = Substitute.For<ILibraryIndexRepository>();
        indexRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<LibraryIndex?>(null));

        var excludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        excludedRepo.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SymbolRejectionReason?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ExcludedSymbol>>([]));

        var diffRepo = Substitute.For<IDiffRepository>();
        diffRepo.GetDiffAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<VersionDiffRecord?>(null));

        var pageRepo = Substitute.For<IPageRepository>();
        pageRepo.GetPagesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PageRecord>>([]));

        var chunkRepo = Substitute.For<IChunkRepository>();
        chunkRepo.GetChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<DocChunk>>([]));

        var exporter = new LibraryExporter(libRepo, profileRepo, indexRepo, excludedRepo, diffRepo, pageRepo, chunkRepo, BuildEmptyBm25Repo());
        var outDir = Path.Combine(Path.GetTempPath(), "saddlerag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "foo-all.srlib.zip");

        var request = new ExportRequest
                          {
                              LibraryId = "foo",
                              Versions = VersionFilter.All,
                              OutputPath = outPath
                          };

        var result = await exporter.ExportAsync(request, progress: null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(8, result.TotalPages);
        Assert.Equal(30, result.TotalChunks);
    }
}
