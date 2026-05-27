// LibraryImporter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Security.Cryptography;
using System.Text.Json;
using SaddleRAG.Core.Models;
using SaddleRAG.Packaging.Internal;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Reads a .srlib.zip bundle and writes it into the receiver's
///     MongoDB. v1: manifest read, sha256 validation, pathological-id
///     guard. Subsequent tasks add conflict check, encoder match,
///     per-version write with rollback, BM25 GridFS re-upload,
///     encoder-mismatch reembed enqueue, overwrite path.
/// </summary>
public sealed class LibraryImporter
{
    public LibraryImporter()
    {
        // Repository dependencies are added in subsequent tasks (Task 16+).
    }

    public async Task<ImportResult> ImportAsync(ImportRequest request,
                                                IProgress<ImportProgress>? progress,
                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.BundlePath);
        if (!File.Exists(request.BundlePath))
            throw new FileNotFoundException("Bundle not found", request.BundlePath);

        using var reader = new ZipBundleReader(request.BundlePath);

        var manifest = await ReadManifestAsync(reader, ct);
        if (manifest.ManifestVersion > BundlePaths.CurrentManifestVersion)
            throw new InvalidOperationException(
                $"Bundle was produced by a newer SaddleRAG (manifestVersion={manifest.ManifestVersion}); upgrade to import.");

        ValidateLibraryId(manifest.Library.Id);

        ValidateAllBlobs(reader, manifest, ct);

        // Subsequent tasks: conflict check, encoder match, per-version import.

        return new ImportResult
                   {
                       VersionsImported = [],
                       OverwrittenVersions = [],
                       BytesFreed = 0,
                       PendingReembedJobIds = [],
                       PartialFailures = [],
                       RecommendedFollowUp = string.Empty
                   };
    }

    private static async Task<BundleManifest> ReadManifestAsync(IBundleReader reader, CancellationToken ct)
    {
        if (!reader.HasEntry(BundlePaths.ManifestFile))
            throw new InvalidOperationException("Bundle is missing manifest.json");
        await using var stream = reader.OpenEntry(BundlePaths.ManifestFile);
        var manifest = await JsonSerializer.DeserializeAsync<BundleManifest>(stream, BundleJsonOptions.Default, ct)
                       ?? throw new InvalidOperationException("manifest.json is empty or invalid");
        return manifest;
    }

    private static void ValidateLibraryId(string id)
    {
        LibraryIdValidator.ValidateLibraryId(id);
    }

    private static void ValidateAllBlobs(IBundleReader reader, BundleManifest manifest, CancellationToken ct)
    {
        foreach (var versionEntry in manifest.Versions)
            ValidateBlobs(reader, versionEntry.Blobs, ct);

        ValidateBlobs(reader, manifest.Blobs, ct);
    }

    private static void ValidateBlobs(IBundleReader reader,
                                      IReadOnlyDictionary<string, BlobInfo> blobs,
                                      CancellationToken ct)
    {
        foreach (var (path, info) in blobs)
            ValidateBlob(reader, path, info, ct);
    }

    private static void ValidateBlob(IBundleReader reader, string path, BlobInfo info, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!reader.HasEntry(path))
            throw new InvalidOperationException($"Bundle manifest references missing entry '{path}'");

        using var stream = reader.OpenEntry(path);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        int read = stream.Read(buffer, 0, buffer.Length);
        while (read > 0)
        {
            hasher.AppendData(buffer, 0, read);
            bytes += read;
            read = stream.Read(buffer, 0, buffer.Length);
        }

        var actual = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actual, info.Sha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Bundle integrity check failed for '{path}': expected {info.Sha256}, got {actual}");
        if (bytes != info.Bytes)
            throw new InvalidOperationException(
                $"Bundle integrity check failed for '{path}': expected {info.Bytes} bytes, got {bytes}");
    }
}
