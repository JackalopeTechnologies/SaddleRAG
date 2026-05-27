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
using SaddleRAG.Packaging;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class LibraryImporterTests
{
    [Fact]
    public async Task RefusesUnknownManifestVersion()
    {
        var path = await CreateSyntheticBundleAsync(manifestVersion: 999);
        var importer = new LibraryImporter();

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
        var importer = new LibraryImporter();

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
        var importer = new LibraryImporter();

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
        var importer = new LibraryImporter();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => importer.ImportAsync(new ImportRequest { BundlePath = path },
                                       progress: null,
                                       ct: TestContext.Current.CancellationToken));
        // The validator throws ArgumentException — match the message that
        // says the library id is invalid.
        Assert.Contains("library id", ex.Message, StringComparison.OrdinalIgnoreCase);
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
