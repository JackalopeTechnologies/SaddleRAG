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
    public LibraryExporter(ILibraryRepository libraryRepository,
                           ILibraryProfileRepository profileRepository,
                           ILibraryIndexRepository indexRepository,
                           IExcludedSymbolsRepository excludedSymbolsRepository,
                           IDiffRepository diffRepository,
                           IPageRepository pageRepository)
    {
        ArgumentNullException.ThrowIfNull(libraryRepository);
        ArgumentNullException.ThrowIfNull(profileRepository);
        ArgumentNullException.ThrowIfNull(indexRepository);
        ArgumentNullException.ThrowIfNull(excludedSymbolsRepository);
        ArgumentNullException.ThrowIfNull(diffRepository);
        ArgumentNullException.ThrowIfNull(pageRepository);
        mLibraryRepository = libraryRepository;
        mProfileRepository = profileRepository;
        mIndexRepository = indexRepository;
        mExcludedSymbolsRepository = excludedSymbolsRepository;
        mDiffRepository = diffRepository;
        mPageRepository = pageRepository;
    }

    private const string TempSuffix = ".tmp";

    private const int ExcludedSymbolsLimit = int.MaxValue;

    private readonly ILibraryRepository mLibraryRepository;
    private readonly ILibraryProfileRepository mProfileRepository;
    private readonly ILibraryIndexRepository mIndexRepository;
    private readonly IExcludedSymbolsRepository mExcludedSymbolsRepository;
    private readonly IDiffRepository mDiffRepository;
    private readonly IPageRepository mPageRepository;

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

        var versionEntries = new List<BundleVersionEntry>();
        for (int i = 0; i < targetVersions.Count; i++)
        {
            progress?.Report(new ExportProgress
                                 {
                                     CurrentVersion = targetVersions[i],
                                     CurrentStep = "version-metadata",
                                     VersionIndex = i,
                                     TotalVersions = targetVersions.Count
                                 });
            var entry = await WriteVersionAsync(writer, library, targetVersions[i], manifestBuilder, ct);
            versionEntries.Add(entry);
        }

        await WriteManifestAsync(writer, library, versionEntries, manifestBuilder, ct);
    }

    private async Task WriteLibraryJsonAsync(IBundleWriter writer,
                                             LibraryRecord library,
                                             ManifestBuilder manifestBuilder,
                                             CancellationToken ct)
    {
        await WriteJsonAsync(writer, manifestBuilder, BundlePaths.LibraryFile, library, ct);
    }

    private async Task<BundleVersionEntry> WriteVersionAsync(IBundleWriter writer,
                                                             LibraryRecord library,
                                                             string version,
                                                             ManifestBuilder manifestBuilder,
                                                             CancellationToken ct)
    {
        var versionRecord = await mLibraryRepository.GetVersionAsync(library.Id, version, ct)
                            ?? throw new InvalidOperationException(
                                $"Version record for '{library.Id}/{version}' not found");

        var versionPath = BundlePaths.VersionFilePath(version, BundlePaths.VersionFile);
        await WriteJsonAsync(writer, manifestBuilder, versionPath, versionRecord, ct);

        var profile = await mProfileRepository.GetAsync(library.Id, version, ct);
        if (profile is not null)
        {
            var profilePath = BundlePaths.VersionFilePath(version, BundlePaths.ProfileFile);
            await WriteJsonAsync(writer, manifestBuilder, profilePath, profile, ct);
        }

        var index = await mIndexRepository.GetAsync(library.Id, version, ct);
        if (index is not null)
        {
            var indexPath = BundlePaths.VersionFilePath(version, BundlePaths.IndexFile);
            await WriteJsonAsync(writer, manifestBuilder, indexPath, index, ct);
        }

        if (versionRecord.PreviousVersion is not null)
        {
            var diff = await mDiffRepository.GetDiffAsync(library.Id, versionRecord.PreviousVersion, version, ct);
            if (diff is not null)
            {
                var diffPath = BundlePaths.VersionFilePath(version, BundlePaths.VersionDiffFile);
                await WriteJsonAsync(writer, manifestBuilder, diffPath, diff, ct);
            }
        }

        var excludedSymbols = await mExcludedSymbolsRepository.ListAsync(library.Id, version, reason: null, ExcludedSymbolsLimit, ct);
        var excludedPath = BundlePaths.VersionFilePath(version, BundlePaths.ExcludedSymbolsFile);
        await WriteJsonlAsync(writer, manifestBuilder, excludedPath, excludedSymbols, ct);

        var pages = await mPageRepository.GetPagesAsync(library.Id, version, ct);
        var pagesPath = BundlePaths.VersionFilePath(version, BundlePaths.PagesFile);
        await WriteJsonlAsync(writer, manifestBuilder, pagesPath, pages, ct);

        var versionDir = BundlePaths.VersionDir(version) + "/";
        var versionBlobs = manifestBuilder
                          .ToDictionary()
                          .Where(kv => kv.Key.StartsWith(versionDir, StringComparison.Ordinal))
                          .ToDictionary(kv => kv.Key, kv => kv.Value);

        return new BundleVersionEntry
                   {
                       Version = version,
                       EmbeddingProviderId = versionRecord.EmbeddingProviderId,
                       EmbeddingModelName = versionRecord.EmbeddingModelName,
                       EmbeddingDimensions = versionRecord.EmbeddingDimensions,
                       PageCount = versionRecord.PageCount,
                       ChunkCount = versionRecord.ChunkCount,
                       // TODO(task-13): set true when BM25 GridFS spillover lands.
                       Bm25HasGridFs = false,
                       Blobs = versionBlobs
                   };
    }

    private async Task WriteManifestAsync(IBundleWriter writer,
                                          LibraryRecord library,
                                          IReadOnlyList<BundleVersionEntry> versions,
                                          ManifestBuilder manifestBuilder,
                                          CancellationToken ct)
    {
        var topLevelBlobs = manifestBuilder
                           .ToDictionary()
                           .Where(kv => !kv.Key.StartsWith(BundlePaths.VersionsDir + "/", StringComparison.Ordinal))
                           .ToDictionary(kv => kv.Key, kv => kv.Value);

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
                               Blobs = topLevelBlobs,
                               Versions = versions
                           };
        await using var entry = writer.OpenEntry(BundlePaths.ManifestFile);
        await JsonSerializer.SerializeAsync(entry, manifest, BundleJsonOptions.Default, ct);
    }

    private async Task WriteJsonAsync<T>(IBundleWriter writer,
                                         ManifestBuilder manifestBuilder,
                                         string entryPath,
                                         T payload,
                                         CancellationToken ct)
    {
        await using var entry = writer.OpenEntry(entryPath);
        await using var hash = manifestBuilder.OpenBlob(entryPath);
        using var tee = new TeeStream(entry, hash, leaveOpen: true);
        await JsonSerializer.SerializeAsync(tee, payload, BundleJsonOptions.Default, ct);
    }

    private async Task WriteJsonlAsync<T>(IBundleWriter writer,
                                           ManifestBuilder manifestBuilder,
                                           string entryPath,
                                           IEnumerable<T> rows,
                                           CancellationToken ct)
    {
        await using var entry = writer.OpenEntry(entryPath);
        await using var hash = manifestBuilder.OpenBlob(entryPath);
        using var tee = new TeeStream(entry, hash, leaveOpen: true);
        await using var jsonl = new JsonlWriter<T>(tee, leaveOpen: true);
        foreach (var row in rows)
            await jsonl.WriteAsync(row, ct);
    }
}
