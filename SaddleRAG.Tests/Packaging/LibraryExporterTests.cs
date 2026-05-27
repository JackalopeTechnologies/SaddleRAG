// LibraryExporterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.IO;
using System.IO.Compression;
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
        Assert.NotNull(archive.GetEntry("manifest.json"));
        var libJsonEntry = archive.GetEntry("library.json");
        Assert.NotNull(libJsonEntry);

        using var libStream = libJsonEntry.Open();
        var lib = JsonSerializer.Deserialize<LibraryRecord>(libStream);
        Assert.NotNull(lib);
        Assert.Equal("foo", lib.Id);
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
}
