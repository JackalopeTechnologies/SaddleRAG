// LibraryExporter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Packaging.Internal;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Streams an indexed library's contents (one or more versions) into
///     a single .srlib.zip bundle on disk. Failure leaves no partial
///     file behind — the writer targets <c>{output}.tmp</c> and the final
///     File.Move is the last step.
/// </summary>
public sealed class LibraryExporter
{
    public LibraryExporter(ILibraryRepository libraryRepository)
    {
        ArgumentNullException.ThrowIfNull(libraryRepository);
        mLibraryRepository = libraryRepository;
    }

    private const string TempSuffix = ".tmp";

    private readonly ILibraryRepository mLibraryRepository;

    public async Task<ExportResult> ExportAsync(ExportRequest request,
                                                IProgress<ExportProgress>? progress,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(request.OutputPath);

        var library = await mLibraryRepository.GetLibraryAsync(request.LibraryId, ct)
                      ?? throw new ArgumentException($"Library '{request.LibraryId}' not found", nameof(request));

        var targetVersions = request.Versions.Resolve(library.CurrentVersion, library.AllVersions);
        if (targetVersions.Count == 0)
            throw new ArgumentException("No versions resolved for export", nameof(request));

        var tempPath = request.OutputPath + TempSuffix;
        var manifestBuilder = new ManifestBuilder();
        long bytesWritten;

        try
        {
            await using (var fileStream = File.Create(tempPath))
            await using (var bundleWriter = new ZipBundleWriter(fileStream, leaveOpen: true))
            {
                await WriteBundleContentsAsync(bundleWriter, library, targetVersions, manifestBuilder, progress, ct);
            }

            bytesWritten = new FileInfo(tempPath).Length;
            File.Move(tempPath, request.OutputPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            throw;
        }

        return new ExportResult
                   {
                       OutputPath = request.OutputPath,
                       BytesWritten = bytesWritten,
                       VersionsExported = targetVersions,
                       TotalPages = 0,
                       TotalChunks = 0
                   };
    }

    private async Task WriteBundleContentsAsync(IBundleWriter writer,
                                                LibraryRecord library,
                                                IReadOnlyList<string> targetVersions,
                                                ManifestBuilder manifestBuilder,
                                                IProgress<ExportProgress>? progress,
                                                CancellationToken ct)
    {
        await WriteLibraryJsonAsync(writer, library, manifestBuilder, ct);

        for (int i = 0; i < targetVersions.Count; i++)
        {
            progress?.Report(new ExportProgress
                                 {
                                     CurrentVersion = targetVersions[i],
                                     CurrentStep = "version-metadata",
                                     VersionIndex = i,
                                     TotalVersions = targetVersions.Count
                                 });
            // Per-version content writes land in subsequent tasks (10-14).
        }

        await WriteManifestAsync(writer, library, ct);
    }

    private async Task WriteLibraryJsonAsync(IBundleWriter writer,
                                             LibraryRecord library,
                                             ManifestBuilder manifestBuilder,
                                             CancellationToken ct)
    {
        await using var entry = writer.OpenEntry(BundlePaths.LibraryFile);
        await using var hash = manifestBuilder.OpenBlob(BundlePaths.LibraryFile);
        using var tee = new TeeStream(entry, hash, leaveOpen: true);
        await JsonSerializer.SerializeAsync(tee, library, BundleJsonOptions.Default, ct);
    }

    private async Task WriteManifestAsync(IBundleWriter writer,
                                          LibraryRecord library,
                                          CancellationToken ct)
    {
        var manifest = new BundleManifest
                           {
                               ManifestVersion = BundlePaths.CurrentManifestVersion,
                               ExporterVersion = typeof(LibraryExporter).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                               CreatedUtc = DateTime.UtcNow,
                               Library = new BundleLibraryInfo
                                             {
                                                 Id = library.Id,
                                                 Name = library.Name,
                                                 Hint = library.Hint
                                             },
                               Versions = []
                           };
        await using var entry = writer.OpenEntry(BundlePaths.ManifestFile);
        await JsonSerializer.SerializeAsync(entry, manifest, BundleJsonOptions.Default, ct);
    }
}
