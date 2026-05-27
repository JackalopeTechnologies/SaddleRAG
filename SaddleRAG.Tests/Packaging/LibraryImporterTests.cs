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
        var importer = new LibraryImporter(libraryRepo, jobRepo, embeddingProvider);

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
        var importer = new LibraryImporter(libraryRepo, jobRepo, embeddingProvider);

        var path = await CreateBundleWithVersionEntryAsync(LibraryId, Version);

        // Overwrite=true must clear the conflict gate; the import returns an empty
        // ImportResult because per-version writes aren't implemented until Task 17.
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
        var importer = new LibraryImporter(libraryRepo, jobRepo, embeddingProvider);

        var path = await CreateBundleWithVersionEntryAsync(LibraryId, Version);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(new ImportRequest { BundlePath = path },
                                       progress: null,
                                       ct: TestContext.Current.CancellationToken));
        Assert.Contains(JobId, ex.Message);
        Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);
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

        return new LibraryImporter(libraryRepo, jobRepo, MakeEmbeddingProvider());
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
    ///     so the importer proceeds past the sha256 gate.
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
