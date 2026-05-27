// LibraryImporter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Security.Cryptography;
using System.Text.Json;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Packaging.Internal;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Reads a .srlib.zip bundle and writes it into the receiver's
///     MongoDB. Tasks 1–15: manifest read, sha256 validation,
///     pathological-id guard. Task 16: conflict check, concurrent-job
///     guard, encoder-match decision. Subsequent tasks add per-version
///     write with rollback, BM25 GridFS re-upload,
///     encoder-mismatch reembed enqueue, overwrite path.
/// </summary>
public sealed class LibraryImporter
{
    #region Dependency fields

    private readonly ILibraryRepository mLibraryRepository;
    private readonly IJobRepository mJobRepository;
    private readonly IEmbeddingProvider mEmbeddingProvider;

    #endregion

    #region Messages constants

    private const string OverwriteHint = "Pass overwrite=true to replace.";
    private const string ConcurrentJobHint = "Wait for it to complete or cancel it before retrying.";

    #endregion

    public LibraryImporter(ILibraryRepository libraryRepository,
                           IJobRepository jobRepository,
                           IEmbeddingProvider embeddingProvider)
    {
        ArgumentNullException.ThrowIfNull(libraryRepository);
        ArgumentNullException.ThrowIfNull(jobRepository);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        mLibraryRepository = libraryRepository;
        mJobRepository = jobRepository;
        mEmbeddingProvider = embeddingProvider;
    }

    #region Active encoder properties

    // ProviderId is surfaced directly from IEmbeddingProvider.
    private string ActiveEncoderProviderId => mEmbeddingProvider.ProviderId;

    private string ActiveEncoderModelName => mEmbeddingProvider.ModelName;

    private int ActiveEncoderDimensions => mEmbeddingProvider.Dimensions;

    #endregion

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

        bool hasVersions = manifest.Versions.Count > 0;
        if (hasVersions)
        {
            // Gate 1 — conflict scan.
            var existingLibrary = await mLibraryRepository.GetLibraryAsync(manifest.Library.Id, ct);
            var existingVersions = existingLibrary?.AllVersions ?? (IReadOnlyList<string>) Array.Empty<string>();

            var conflicting = manifest.Versions
                                      .Select(v => v.Version)
                                      .Where(v => existingVersions.Contains(v))
                                      .ToList();
            if (conflicting.Count > 0 && !request.Overwrite)
                throw new InvalidOperationException(
                    $"Versions already present on receiver: {string.Join(", ", conflicting)}. {OverwriteHint}");

            // Gate 2 — concurrent-job guard: refuse if any non-terminal job already
            // targets any of our (library, version) tuples.
            foreach (var manifestVersion in manifest.Versions)
            {
                var running = await mJobRepository.ListActiveAsync(manifest.Library.Id,
                                                                   manifestVersion.Version,
                                                                   ct: ct);
                if (running.Count > 0)
                {
                    var first = running[0];
                    throw new InvalidOperationException(
                        $"Cannot import: job {first.Id} (type={first.JobType}, status={first.Status}) is already " +
                        $"running for {manifest.Library.Id}/{manifestVersion.Version}. {ConcurrentJobHint}");
                }
            }

            // Gate 3 — encoder-match decision.
            // The exporter guarantees all versions in a single bundle share one encoder,
            // so comparing against the first version entry is sufficient.
            var bundleEncoder = manifest.Versions[0];
            bool encoderMatches =
                string.Equals(bundleEncoder.EmbeddingProviderId, ActiveEncoderProviderId, StringComparison.Ordinal)
                && string.Equals(bundleEncoder.EmbeddingModelName, ActiveEncoderModelName, StringComparison.Ordinal)
                && bundleEncoder.EmbeddingDimensions == ActiveEncoderDimensions;

            // encoderMatches is computed here; Task 17+ will branch on it to decide whether
            // to import vectors as-is or enqueue a reembed job.
            _ = encoderMatches;
        }

        // Subsequent tasks: per-version write with rollback, BM25 GridFS re-upload,
        // encoder-mismatch reembed enqueue, overwrite path.

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
