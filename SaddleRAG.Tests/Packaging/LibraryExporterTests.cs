// LibraryExporterTests.cs
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
using NSubstitute;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Packaging;
using SaddleRAG.Tests.Packaging.Fixtures;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class LibraryExporterTests
{
    private static (LibraryExporter exporter, ILibraryRepository libRepo, string outDir) BuildExporter(
        LibraryRecord library)
    {
        var libRepo = Substitute.For<ILibraryRepository>();
        libRepo.GetLibraryAsync(library.Id, Arg.Any<CancellationToken>()).Returns(Task.FromResult<LibraryRecord?>(library));

        var exporter = new LibraryExporter(libRepo);
        var outDir = Path.Combine(Path.GetTempPath(), "saddlerag-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        return (exporter, libRepo, outDir);
    }

    [Fact]
    public async Task EmitsManifestAndLibraryJson()
    {
        var library = PackagingFixtures.MakeLibrary("foo", "1.0");
        var (exporter, _, outDir) = BuildExporter(library);
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
        var (exporter, _, outDir) = BuildExporter(library);
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

        var exporter = new LibraryExporter(libRepo);
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
}
