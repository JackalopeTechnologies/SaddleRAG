// LibraryImporterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Packaging;
using SaddleRAG.Tests.Packaging.Fixtures;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class LibraryImporterTests
{
    [Fact]
    public async Task RefusesUnknownManifestVersion()
    {
        var path = await CreateSyntheticBundleAsync(manifestVersion: 999);
        var importer = MakeImporter();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(new ImportRequest { BundlePath = path },
                                       progress: null,
                                       ct: TestContext.Current.CancellationToken));
        Assert.Contains("newer SaddleRAG", ex.Message);
    }

    [Fact]
    public async Task RefusesBundleWithMissingReferencedEntry()
    {
        // Build a synthetic bundle whose manifest references "library.json" with a
        // computed sha256, but the actual zip entry does NOT exist.
        var path = await CreateBundleWithMissingEntryAsync();
        var importer = MakeImporter();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(new ImportRequest { BundlePath = path },
                                       progress: null,
                                       ct: TestContext.Current.CancellationToken));
        Assert.Contains("missing entry", ex.Message);
    }

    [Fact]
    public async Task RefusesBundleWithSha256Mismatch()
    {
        // Build a synthetic bundle whose manifest claims a sha256 that doesn't
        // match the actual entry bytes.
        var path = await CreateBundleWithBadHashAsync();
        var importer = MakeImporter();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(new ImportRequest { BundlePath = path },
                                       progress: null,
                                       ct: TestContext.Current.CancellationToken));
        Assert.Contains("integrity check failed", ex.Message);
    }

    [Fact]
    public async Task RefusesBundleWithPathologicalLibraryId()
    {
        // Build a synthetic bundle whose manifest's library.id contains path-traversal.
        var path = await CreateSyntheticBundleAsync(manifestVersion: 1, libraryId: "../etc/passwd");
        var importer = MakeImporter();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => importer.ImportAsync(new ImportRequest { BundlePath = path },
                                       progress: null,
                                       ct: TestContext.Current.CancellationToken));
        // The validator throws ArgumentException — match the message that
        // says the library id is invalid.
        Assert.Contains("library id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesIfVersionExistsAndOverwriteFalse()
    {
        const string LibraryId = "foo";
        const string Version = "1.0";

        var libraryRepo = Substitute.For<ILibraryRepository>();
        libraryRepo.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = LibraryId,
                                    Name = LibraryId,
                                    Hint = "test",
                                    CurrentVersion = Version,
                                    AllVersions = [Version]
                                });

        var jobRepo = Substitute.For<IJobRepository>();
        jobRepo.ListActiveAsync(Arg.Any<string>(),
                                Arg.Any<string?>(),
                                Arg.Any<JobType?>(),
                                Arg.Any<CancellationToken>())
               .Returns(Array.Empty<JobRecord>() as IReadOnlyList<JobRecord>);

        var embeddingProvider = MakeEmbeddingProvider();
        var importer = MakeImporter(libraryRepo, jobRepo, embeddingProvider);

        var path = await CreateBundleWithVersionEntryAsync(LibraryId, Version);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(new ImportRequest { BundlePath = path, Overwrite = false },
                                       progress: null,
                                       ct: TestContext.Current.CancellationToken));
        Assert.Contains(Version, ex.Message);
        Assert.Contains("overwrite=true", ex.Message);
    }

    [Fact]
    public async Task SucceedsConflictCheckWhenOverwriteTrue()
    {
        const string LibraryId = "foo";
        const string Version = "1.0";

        var libraryRepo = Substitute.For<ILibraryRepository>();
        libraryRepo.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = LibraryId,
                                    Name = LibraryId,
                                    Hint = "test",
                                    CurrentVersion = Version,
                                    AllVersions = [Version]
                                });

        var jobRepo = Substitute.For<IJobRepository>();
        jobRepo.ListActiveAsync(Arg.Any<string>(),
                                Arg.Any<string?>(),
                                Arg.Any<JobType?>(),
                                Arg.Any<CancellationToken>())
               .Returns(Array.Empty<JobRecord>() as IReadOnlyList<JobRecord>);

        var embeddingProvider = MakeEmbeddingProvider();
        var importer = MakeImporter(libraryRepo, jobRepo, embeddingProvider);

        var path = await CreateBundleWithVersionEntryAsync(LibraryId, Version);

        // Overwrite=true clears the conflict gate. The bundle lacks version.json
        // so the write attempt fails; the version lands in PartialFailures, not
        // VersionsImported.
        var result = await importer.ImportAsync(new ImportRequest { BundlePath = path, Overwrite = true },
                                                progress: null,
                                                ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Empty(result.VersionsImported);
    }

    [Fact]
    public async Task RefusesIfRunningJobExistsForTargetVersion()
    {
        const string LibraryId = "foo";
        const string Version = "1.0";
        const string JobId = "job-abc-123";

        var libraryRepo = Substitute.For<ILibraryRepository>();
        libraryRepo.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                   .Returns((LibraryRecord?) null);

        var runningJob = new JobRecord
                             {
                                 Id = JobId,
                                 JobType = JobType.Scrape,
                                 Status = JobStatus.Running,
                                 LibraryId = LibraryId,
                                 Version = Version
                             };

        var jobRepo = Substitute.For<IJobRepository>();
        jobRepo.ListActiveAsync(LibraryId,
                                Version,
                                Arg.Any<JobType?>(),
                                Arg.Any<CancellationToken>())
               .Returns(new[] { runningJob } as IReadOnlyList<JobRecord>);

        var embeddingProvider = MakeEmbeddingProvider();
        var importer = MakeImporter(libraryRepo, jobRepo, embeddingProvider);

        var path = await CreateBundleWithVersionEntryAsync(LibraryId, Version);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(new ImportRequest { BundlePath = path },
                                       progress: null,
                                       ct: TestContext.Current.CancellationToken));
        Assert.Contains(JobId, ex.Message);
        Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RoundTripsSingleVersionWhenEncoderMatches()
    {
        const string LibraryId = "test-lib";
        const string Version = "1.0";
        const int Dim = PackagingFixtures.DefaultDim;
        const int PageCount = 2;
        const int ChunkCount = 3;

        // Build fixture data.
        var library = PackagingFixtures.MakeLibrary(LibraryId, Version);
        var versionRecord = PackagingFixtures.MakeVersion(LibraryId, Version, PageCount, ChunkCount, Dim);
        var pages = PackagingFixtures.MakePages(LibraryId, Version, PageCount);
        var chunks = PackagingFixtures.MakeChunks(LibraryId, Version, ChunkCount, Dim);

        // Wire exporter mocks.
        var exportLibraryRepo = Substitute.For<ILibraryRepository>();
        exportLibraryRepo.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                         .Returns(library);
        exportLibraryRepo.GetVersionAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                         .Returns(versionRecord);

        var exportPageRepo = Substitute.For<IPageRepository>();
        exportPageRepo.GetPagesAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                      .Returns(pages);

        var exportChunkRepo = Substitute.For<IChunkRepository>();
        exportChunkRepo.GetChunksAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                       .Returns(chunks);

        var exportProfileRepo = Substitute.For<ILibraryProfileRepository>();
        exportProfileRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                         .Returns((LibraryProfile?) null);

        var exportIndexRepo = Substitute.For<ILibraryIndexRepository>();
        exportIndexRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                       .Returns((LibraryIndex?) null);

        var exportDiffRepo = Substitute.For<IDiffRepository>();
        var exportExcludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        exportExcludedRepo.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SymbolRejectionReason?>(),
                                     Arg.Any<int>(), Arg.Any<CancellationToken>())
                          .Returns(Array.Empty<ExcludedSymbol>() as IReadOnlyList<ExcludedSymbol>);

        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        bm25Repo.GetAllShardsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<Bm25Shard>() as IReadOnlyList<Bm25Shard>);

        var bundlePath = await CreateValidSingleVersionBundleAsync(
            exportLibraryRepo, exportProfileRepo, exportIndexRepo, exportExcludedRepo,
            exportDiffRepo, exportPageRepo, exportChunkRepo, bm25Repo,
            LibraryId, Version);

        // Wire receiver-side importer mocks — library doesn't exist yet.
        var importLibraryRepo = Substitute.For<ILibraryRepository>();
        importLibraryRepo.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                         .Returns((LibraryRecord?) null);

        var importJobRepo = Substitute.For<IJobRepository>();
        importJobRepo.ListActiveAsync(Arg.Any<string>(),
                                      Arg.Any<string?>(),
                                      Arg.Any<JobType?>(),
                                      Arg.Any<CancellationToken>())
                     .Returns(Array.Empty<JobRecord>() as IReadOnlyList<JobRecord>);

        // Encoder matches the bundle.
        var importEmbeddingProvider = MakeEmbeddingProvider(
            providerId: versionRecord.EmbeddingProviderId,
            modelName: versionRecord.EmbeddingModelName,
            dimensions: versionRecord.EmbeddingDimensions);

        var importPageRepo = Substitute.For<IPageRepository>();
        var importChunkRepo = Substitute.For<IChunkRepository>();
        var importProfileRepo = Substitute.For<ILibraryProfileRepository>();
        var importIndexRepo = Substitute.For<ILibraryIndexRepository>();
        var importExcludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        var importDiffRepo = Substitute.For<IDiffRepository>();

        var importer = new LibraryImporter(importLibraryRepo, importJobRepo, importEmbeddingProvider,
                                           importProfileRepo, importIndexRepo, importExcludedRepo,
                                           importDiffRepo, importPageRepo, importChunkRepo);

        var result = await importer.ImportAsync(new ImportRequest { BundlePath = bundlePath },
                                                progress: null,
                                                ct: TestContext.Current.CancellationToken);

        // Version must be in VersionsImported, no partial failures.
        Assert.Contains(Version, result.VersionsImported);
        Assert.Empty(result.PartialFailures);

        // Version record was upserted.
        await importLibraryRepo.Received(1).UpsertVersionAsync(
            Arg.Is<LibraryVersionRecord>(r => r.LibraryId == LibraryId && r.Version == Version),
            Arg.Any<CancellationToken>());

        // Pages were inserted.
        await importPageRepo.Received(PageCount).UpsertPageAsync(
            Arg.Is<PageRecord>(p => p.LibraryId == LibraryId && p.Version == Version),
            Arg.Any<CancellationToken>());

        // Chunks were inserted with non-null embeddings matching dimensions.
        await importChunkRepo.Received().InsertChunksAsync(
            Arg.Is<IReadOnlyList<DocChunk>>(cs =>
                cs.Count == ChunkCount
                && cs.All(c => c.Embedding != null && c.Embedding.Length == Dim)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RollsBackVersionOnMidImportFailure()
    {
        const string LibraryId = "test-lib";
        const string Version = "1.0";
        const int Dim = PackagingFixtures.DefaultDim;
        const int PageCount = 2;
        const int ChunkCount = 3;

        // Build fixture data and a real bundle.
        var library = PackagingFixtures.MakeLibrary(LibraryId, Version);
        var versionRecord = PackagingFixtures.MakeVersion(LibraryId, Version, PageCount, ChunkCount, Dim);
        var pages = PackagingFixtures.MakePages(LibraryId, Version, PageCount);
        var chunks = PackagingFixtures.MakeChunks(LibraryId, Version, ChunkCount, Dim);

        var exportLibraryRepo = Substitute.For<ILibraryRepository>();
        exportLibraryRepo.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                         .Returns(library);
        exportLibraryRepo.GetVersionAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                         .Returns(versionRecord);

        var exportPageRepo = Substitute.For<IPageRepository>();
        exportPageRepo.GetPagesAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                      .Returns(pages);

        var exportChunkRepo = Substitute.For<IChunkRepository>();
        exportChunkRepo.GetChunksAsync(LibraryId, Version, Arg.Any<CancellationToken>())
                       .Returns(chunks);

        var exportProfileRepo = Substitute.For<ILibraryProfileRepository>();
        exportProfileRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                         .Returns((LibraryProfile?) null);

        var exportIndexRepo = Substitute.For<ILibraryIndexRepository>();
        exportIndexRepo.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                       .Returns((LibraryIndex?) null);

        var exportDiffRepo = Substitute.For<IDiffRepository>();
        var exportExcludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        exportExcludedRepo.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SymbolRejectionReason?>(),
                                     Arg.Any<int>(), Arg.Any<CancellationToken>())
                          .Returns(Array.Empty<ExcludedSymbol>() as IReadOnlyList<ExcludedSymbol>);

        var bm25Repo = Substitute.For<IBm25ShardRepository>();
        bm25Repo.GetAllShardsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<Bm25Shard>() as IReadOnlyList<Bm25Shard>);

        var bundlePath = await CreateValidSingleVersionBundleAsync(
            exportLibraryRepo, exportProfileRepo, exportIndexRepo, exportExcludedRepo,
            exportDiffRepo, exportPageRepo, exportChunkRepo, bm25Repo,
            LibraryId, Version);

        // Receiver importer — chunk insert throws.
        var importLibraryRepo = Substitute.For<ILibraryRepository>();
        importLibraryRepo.GetLibraryAsync(LibraryId, Arg.Any<CancellationToken>())
                         .Returns((LibraryRecord?) null);
        importLibraryRepo.DeleteVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                         .Returns(new DeleteVersionResult(1, false, null));

        var importJobRepo = Substitute.For<IJobRepository>();
        importJobRepo.ListActiveAsync(Arg.Any<string>(),
                                      Arg.Any<string?>(),
                                      Arg.Any<JobType?>(),
                                      Arg.Any<CancellationToken>())
                     .Returns(Array.Empty<JobRecord>() as IReadOnlyList<JobRecord>);

        var importEmbeddingProvider = MakeEmbeddingProvider(
            providerId: versionRecord.EmbeddingProviderId,
            modelName: versionRecord.EmbeddingModelName,
            dimensions: versionRecord.EmbeddingDimensions);

        var importPageRepo = Substitute.For<IPageRepository>();
        importPageRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                      .Returns(0L);

        var importChunkRepo = Substitute.For<IChunkRepository>();
        importChunkRepo.InsertChunksAsync(Arg.Any<IReadOnlyList<DocChunk>>(), Arg.Any<CancellationToken>())
                       .Returns<Task>(_ => Task.FromException(new InvalidOperationException("simulated chunk write failure")));
        importChunkRepo.DeleteChunksAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                       .Returns(0L);

        var importProfileRepo = Substitute.For<ILibraryProfileRepository>();
        var importIndexRepo = Substitute.For<ILibraryIndexRepository>();
        var importExcludedRepo = Substitute.For<IExcludedSymbolsRepository>();
        importExcludedRepo.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                          .Returns(0L);

        var importDiffRepo = Substitute.For<IDiffRepository>();

        var importer = new LibraryImporter(importLibraryRepo, importJobRepo, importEmbeddingProvider,
                                           importProfileRepo, importIndexRepo, importExcludedRepo,
                                           importDiffRepo, importPageRepo, importChunkRepo);

        var result = await importer.ImportAsync(new ImportRequest { BundlePath = bundlePath },
                                                progress: null,
                                                ct: TestContext.Current.CancellationToken);

        // The version is NOT in VersionsImported.
        Assert.DoesNotContain(Version, result.VersionsImported);

        // The failure is recorded.
        Assert.Single(result.PartialFailures);
        Assert.Equal(Version, result.PartialFailures[0].Version);

        // Rollback: pages and chunks deleted by version.
        await importPageRepo.Received(1).DeleteAsync(LibraryId, Version, Arg.Any<CancellationToken>());
        await importChunkRepo.Received(1).DeleteChunksAsync(LibraryId, Version, Arg.Any<CancellationToken>());

        // Rollback: version record deleted.
        await importLibraryRepo.Received(1)
                               .DeleteVersionAsync(LibraryId, Version, Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static LibraryImporter MakeImporter()
    {
        var libraryRepo = Substitute.For<ILibraryRepository>();
        libraryRepo.GetLibraryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns((LibraryRecord?) null);

        var jobRepo = Substitute.For<IJobRepository>();
        jobRepo.ListActiveAsync(Arg.Any<string>(),
                                Arg.Any<string?>(),
                                Arg.Any<JobType?>(),
                                Arg.Any<CancellationToken>())
               .Returns(Array.Empty<JobRecord>() as IReadOnlyList<JobRecord>);

        return MakeImporter(libraryRepo, jobRepo, MakeEmbeddingProvider());
    }

    private static LibraryImporter MakeImporter(ILibraryRepository libraryRepo,
                                                 IJobRepository jobRepo,
                                                 IEmbeddingProvider embeddingProvider)
    {
        return new LibraryImporter(
            libraryRepo,
            jobRepo,
            embeddingProvider,
            Substitute.For<ILibraryProfileRepository>(),
            Substitute.For<ILibraryIndexRepository>(),
            Substitute.For<IExcludedSymbolsRepository>(),
            Substitute.For<IDiffRepository>(),
            Substitute.For<IPageRepository>(),
            Substitute.For<IChunkRepository>());
    }

    private static IEmbeddingProvider MakeEmbeddingProvider(string providerId = "onnx-local",
                                                             string modelName = "test-model",
                                                             int dimensions = 384)
    {
        var provider = Substitute.For<IEmbeddingProvider>();
        provider.ProviderId.Returns(providerId);
        provider.ModelName.Returns(modelName);
        provider.Dimensions.Returns(dimensions);
        return provider;
    }

    /// <summary>
    ///     Exports a real bundle using <see cref="LibraryExporter" /> wired
    ///     with the supplied mocked repos. Returns the path to the bundle file.
    /// </summary>
    private static async Task<string> CreateValidSingleVersionBundleAsync(
        ILibraryRepository libraryRepo,
        ILibraryProfileRepository profileRepo,
        ILibraryIndexRepository indexRepo,
        IExcludedSymbolsRepository excludedRepo,
        IDiffRepository diffRepo,
        IPageRepository pageRepo,
        IChunkRepository chunkRepo,
        IBm25ShardRepository bm25Repo,
        string libraryId,
        string version)
    {
        var exporter = new LibraryExporter(libraryRepo, profileRepo, indexRepo, excludedRepo,
                                           diffRepo, pageRepo, chunkRepo, bm25Repo);

        var outputPath = Path.Combine(Path.GetTempPath(), $"saddlerag-test-{Guid.NewGuid():N}.srlib.zip");
        await exporter.ExportAsync(
            new ExportRequest
            {
                LibraryId = libraryId,
                OutputPath = outputPath,
                Versions = VersionFilter.All
            },
            progress: null,
            ct: TestContext.Current.CancellationToken);

        return outputPath;
    }

    private static async Task<string> CreateSyntheticBundleAsync(int manifestVersion, string libraryId = "valid-id")
    {
        var path = Path.Combine(Path.GetTempPath(), $"saddlerag-test-{Guid.NewGuid():N}.srlib.zip");

        using (var fs = File.Create(path))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifest = new BundleManifest
                               {
                                   ManifestVersion = manifestVersion,
                                   ExporterVersion = "test",
                                   CreatedUtc = DateTime.UtcNow,
                                   Library = new BundleLibraryInfo { Id = libraryId, Name = libraryId, Hint = "test" },
                                   Blobs = new Dictionary<string, BlobInfo>(),
                                   Versions = []
                               };
            await using var entry = archive.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(entry, manifest, BundleJsonOptions.Default);
        }

        return path;
    }

    /// <summary>
    ///     Builds a valid bundle that contains a single version entry.
    ///     Both the per-version blob and manifest blob hashes are correct
    ///     so the importer proceeds past the sha256 gate. The bundle lacks
    ///     version.json intentionally — used only for gate-check tests.
    /// </summary>
    private static async Task<string> CreateBundleWithVersionEntryAsync(string libraryId,
                                                                         string version,
                                                                         string providerId = "onnx-local",
                                                                         string modelName = "test-model",
                                                                         int dimensions = 384)
    {
        var path = Path.Combine(Path.GetTempPath(), $"saddlerag-test-{Guid.NewGuid():N}.srlib.zip");

        // Build a minimal library.json blob.
        var libContent = System.Text.Encoding.UTF8.GetBytes("{\"id\":\"" + libraryId + "\"}");
        var libSha = Convert.ToHexString(SHA256.HashData(libContent)).ToLowerInvariant();
        var libBlobPath = "library.json";

        // Build a minimal per-version chunks blob.
        var chunkContent = System.Text.Encoding.UTF8.GetBytes("{}");
        var chunkSha = Convert.ToHexString(SHA256.HashData(chunkContent)).ToLowerInvariant();
        var chunkBlobPath = $"{libraryId}/{version}/chunks.jsonl";

        var versionEntry = new BundleVersionEntry
                               {
                                   Version = version,
                                   EmbeddingProviderId = providerId,
                                   EmbeddingModelName = modelName,
                                   EmbeddingDimensions = dimensions,
                                   PageCount = 0,
                                   ChunkCount = 0,
                                   Bm25HasGridFs = false,
                                   Blobs = new Dictionary<string, BlobInfo>
                                               {
                                                   [chunkBlobPath] = new BlobInfo { Sha256 = chunkSha, Bytes = chunkContent.Length }
                                               }
                               };

        var manifest = new BundleManifest
                           {
                               ManifestVersion = 1,
                               ExporterVersion = "test",
                               CreatedUtc = DateTime.UtcNow,
                               Library = new BundleLibraryInfo { Id = libraryId, Name = libraryId, Hint = "test" },
                               Blobs = new Dictionary<string, BlobInfo>
                                           {
                                               [libBlobPath] = new BlobInfo { Sha256 = libSha, Bytes = libContent.Length }
                                           },
                               Versions = [versionEntry]
                           };

        using (var fs = File.Create(path))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            await using (var libEntry = archive.CreateEntry(libBlobPath).Open())
                await libEntry.WriteAsync(libContent);

            await using (var chunkEntry = archive.CreateEntry(chunkBlobPath).Open())
                await chunkEntry.WriteAsync(chunkContent);

            await using var manifestEntry = archive.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(manifestEntry, manifest, BundleJsonOptions.Default);
        }

        return path;
    }

    private static async Task<string> CreateBundleWithMissingEntryAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"saddlerag-test-{Guid.NewGuid():N}.srlib.zip");
        var fakeSha = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant();

        using (var fs = File.Create(path))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifest = new BundleManifest
                               {
                                   ManifestVersion = 1,
                                   ExporterVersion = "test",
                                   CreatedUtc = DateTime.UtcNow,
                                   Library = new BundleLibraryInfo { Id = "valid-id", Name = "valid-id", Hint = "test" },
                                   Blobs = new Dictionary<string, BlobInfo>
                                               {
                                                   ["library.json"] = new BlobInfo { Sha256 = fakeSha, Bytes = 3 }
                                               },
                                   Versions = []
                               };
            await using var entry = archive.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(entry, manifest, BundleJsonOptions.Default);
            // No library.json entry written.
        }

        return path;
    }

    private static async Task<string> CreateBundleWithBadHashAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"saddlerag-test-{Guid.NewGuid():N}.srlib.zip");
        var realContent = new byte[] { 1, 2, 3 };
        var wrongSha = Convert.ToHexString(SHA256.HashData(new byte[] { 4, 5, 6 })).ToLowerInvariant();

        using (var fs = File.Create(path))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            await using (var libEntry = archive.CreateEntry("library.json").Open())
                await libEntry.WriteAsync(realContent);

            var manifest = new BundleManifest
                               {
                                   ManifestVersion = 1,
                                   ExporterVersion = "test",
                                   CreatedUtc = DateTime.UtcNow,
                                   Library = new BundleLibraryInfo { Id = "valid-id", Name = "valid-id", Hint = "test" },
                                   Blobs = new Dictionary<string, BlobInfo>
                                               {
                                                   ["library.json"] = new BlobInfo { Sha256 = wrongSha, Bytes = realContent.Length }
                                               },
                                   Versions = []
                               };
            await using var entry = archive.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(entry, manifest, BundleJsonOptions.Default);
        }

        return path;
    }
}
