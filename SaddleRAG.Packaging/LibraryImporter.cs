// LibraryImporter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Driver;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Packaging.Internal;

#endregion

namespace SaddleRAG.Packaging;

/// <summary>
///     Reads a .srlib.zip bundle and writes it into the receiver's
///     MongoDB. Tasks 1–15: manifest read, sha256 validation,
///     pathological-id guard. Task 16: conflict check, concurrent-job
///     guard, encoder-match decision. Task 17: per-version write with
///     rollback (encoder-match path). Task 18 adds BM25 GridFS re-upload,
///     Task 19 encoder-mismatch reembed enqueue, Task 20 overwrite path.
/// </summary>
public sealed class LibraryImporter
{
    #region Dependency fields

    private readonly ILibraryRepository mLibraryRepository;
    private readonly IJobRepository mJobRepository;
    private readonly IEmbeddingProvider mEmbeddingProvider;
    private readonly ILibraryProfileRepository mProfileRepository;
    private readonly ILibraryIndexRepository mIndexRepository;
    private readonly IExcludedSymbolsRepository mExcludedSymbolsRepository;
    private readonly IDiffRepository mDiffRepository;
    private readonly IPageRepository mPageRepository;
    private readonly IChunkRepository mChunkRepository;
    private readonly IBm25ShardRepository mBm25Repository;
    private readonly ICollectionCompactor? mCompactor;
    private readonly Func<string?, IMongoDatabase>? mDatabaseResolver;

    #endregion

    #region Constants

    private const string OverwriteHint = "Pass overwrite=true to replace.";
    private const string ConcurrentJobHint = "Wait for it to complete or cancel it before retrying.";
    private const int PageBatchSize = 256;
    private const int ChunkBatchSize = 256;
    private const string ReembedItemsLabel = "chunks";
    private const string FollowUpSeparator = "; ";
    private const int BytesPerFloat = 4;
    private const int EstimatedBytesPerPage = 50_000;
    private const string PurgeFailedReasonFormat =
        "Overwrite purge: failed to delete {0}; stale data from the previous copy of this version may remain.";
    private const string PurgeTargetGridFsBlobFormat = "GridFS blob {0}";
    private const string PurgeTargetBm25 = "BM25 shards";
    private const string PurgeTargetChunks = "chunks";
    private const string PurgeTargetPages = "pages";
    private const string PurgeTargetExcludedSymbols = "excluded symbols";
    private const string PurgeTargetIndex = "index";
    private const string PurgeTargetProfile = "profile";
    private const string PurgeTargetVersionRecord = "version record";

    #endregion

    public LibraryImporter(ILibraryRepository libraryRepository,
                           IJobRepository jobRepository,
                           IEmbeddingProvider embeddingProvider,
                           ILibraryProfileRepository profileRepository,
                           ILibraryIndexRepository indexRepository,
                           IExcludedSymbolsRepository excludedSymbolsRepository,
                           IDiffRepository diffRepository,
                           IPageRepository pageRepository,
                           IChunkRepository chunkRepository,
                           IBm25ShardRepository bm25Repository,
                           ICollectionCompactor? compactor = null,
                           Func<string?, IMongoDatabase>? databaseResolver = null)
    {
        ArgumentNullException.ThrowIfNull(libraryRepository);
        ArgumentNullException.ThrowIfNull(jobRepository);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(profileRepository);
        ArgumentNullException.ThrowIfNull(indexRepository);
        ArgumentNullException.ThrowIfNull(excludedSymbolsRepository);
        ArgumentNullException.ThrowIfNull(diffRepository);
        ArgumentNullException.ThrowIfNull(pageRepository);
        ArgumentNullException.ThrowIfNull(chunkRepository);
        ArgumentNullException.ThrowIfNull(bm25Repository);
        mLibraryRepository = libraryRepository;
        mJobRepository = jobRepository;
        mEmbeddingProvider = embeddingProvider;
        mProfileRepository = profileRepository;
        mIndexRepository = indexRepository;
        mExcludedSymbolsRepository = excludedSymbolsRepository;
        mDiffRepository = diffRepository;
        mPageRepository = pageRepository;
        mChunkRepository = chunkRepository;
        mBm25Repository = bm25Repository;
        mCompactor = compactor;
        mDatabaseResolver = databaseResolver;
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

        var versionsImported = new List<string>();
        var partialFailures = new List<ImportPartialFailure>();
        var overwroteVersions = new List<string>();
        long bytesFreed = 0;
        bool encoderMatches = true;

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

            // Gate 2 — concurrent-job guard.
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
            var bundleEncoder = manifest.Versions[0];
            encoderMatches =
                string.Equals(bundleEncoder.EmbeddingProviderId, ActiveEncoderProviderId, StringComparison.Ordinal)
                && string.Equals(bundleEncoder.EmbeddingModelName, ActiveEncoderModelName, StringComparison.Ordinal)
                && bundleEncoder.EmbeddingDimensions == ActiveEncoderDimensions;

            // Overwrite: purge pre-existing versions before writing new data.
            // This ordering is intentional: purge runs before the per-version write loop
            // so that rollback on a subsequent write failure can safely use
            // DeleteAsync(libraryId, version) without risk of removing pre-existing rows
            // — there are none at that point.
            if (request.Overwrite && conflicting.Count > 0)
            {
                foreach (var conflictingVersion in conflicting)
                {
                    var freed = await PurgeVersionAsync(manifest.Library.Id,
                                                        manifest.Versions.First(v => v.Version == conflictingVersion),
                                                        partialFailures,
                                                        ct);
                    bytesFreed += freed;
                    overwroteVersions.Add(conflictingVersion);
                }
            }

            // Per-version write loop with rollback.
            for (int i = 0; i < manifest.Versions.Count; i++)
            {
                var versionEntry = manifest.Versions[i];
                progress?.Report(new ImportProgress
                                     {
                                         CurrentVersion = versionEntry.Version,
                                         CurrentStep = "writing",
                                         VersionIndex = i,
                                         TotalVersions = manifest.Versions.Count
                                     });

                var log = new VersionWriteLog();
                try
                {
                    await WriteVersionAsync(reader, manifest.Library.Id, versionEntry, encoderMatches, log, ct);
                    versionsImported.Add(versionEntry.Version);
                }
                catch (OperationCanceledException)
                {
                    await RollbackVersionAsync(log, manifest.Library.Id, versionEntry.Version, ct);
                    throw;
                }
                catch (Exception ex)
                {
                    await RollbackVersionAsync(log, manifest.Library.Id, versionEntry.Version, ct);
                    partialFailures.Add(new ImportPartialFailure
                                            {
                                                Version = versionEntry.Version,
                                                Reason = ex.Message
                                            });
                    break;
                }
            }
        }

        // Library record upsert — merge existing AllVersions with newly imported versions.
        if (versionsImported.Count > 0)
            await UpsertLibraryRecordAsync(reader, manifest, versionsImported, ct);

        var pendingReembedJobIds = new List<string>();

        if (!encoderMatches && versionsImported.Count > 0)
        {
            foreach (var version in versionsImported)
            {
                var jobId = await EnqueueReembedAsync(manifest.Library.Id, version, ct);
                pendingReembedJobIds.Add(jobId);
            }
        }

        if (request.Compact && overwroteVersions.Count > 0 && mCompactor != null && mDatabaseResolver != null)
        {
            var database = mDatabaseResolver(request.Profile);
            foreach (var name in mCompactor.DefaultHotCollections)
                await mCompactor.CompactAsync(database, name, ct);
        }

        var recommendedFollowUp = BuildRecommendedFollowUp(pendingReembedJobIds,
                                                             bytesFreed: bytesFreed,
                                                             overwroteAny: overwroteVersions.Count > 0);

        return new ImportResult
                   {
                       LibraryId = manifest.Library.Id,
                       VersionsImported = versionsImported,
                       OverwrittenVersions = overwroteVersions,
                       BytesFreed = bytesFreed,
                       PendingReembedJobIds = pendingReembedJobIds,
                       PartialFailures = partialFailures,
                       RecommendedFollowUp = recommendedFollowUp
                   };
    }

    private async Task<long> PurgeVersionAsync(string libraryId,
                                               BundleVersionEntry versionEntry,
                                               ICollection<ImportPartialFailure> partialFailures,
                                               CancellationToken ct)
    {
        // Estimate bytes freed: embedding vectors + page content.
        long estimated = (long) versionEntry.ChunkCount * versionEntry.EmbeddingDimensions * BytesPerFloat
                         + (long) versionEntry.PageCount * EstimatedBytesPerPage;

        // Collect GridFS blob ids from existing shards before deleting shards.
        var existingShards = await mBm25Repository.GetAllShardsAsync(libraryId, versionEntry.Version, ct);
        var gridFsIdsToDelete = new List<string>();
        foreach (var shard in existingShards)
        {
            if (shard.ShardGridFsRef is not null)
                gridFsIdsToDelete.Add(shard.ShardGridFsRef);
            foreach (var externalRef in shard.ExternalTerms.Values)
                gridFsIdsToDelete.Add(externalRef);
        }

        // Unlike rollback, a swallowed purge failure would let the import
        // report clean success over stale data — record every miss as a
        // partial failure on the result (issue #147).
        var failedTargets = new List<string>();

        // Delete each referenced GridFS blob before deleting the shard rows.
        foreach (var blobId in gridFsIdsToDelete)
        {
            if (!await TryDeleteBm25BlobAsync(blobId, ct))
                failedTargets.Add(string.Format(PurgeTargetGridFsBlobFormat, blobId));
        }

        // Delete all data collections for this version.
        if (!await TryDeleteAsync(() => mBm25Repository.DeleteAsync(libraryId, versionEntry.Version, ct)))
            failedTargets.Add(PurgeTargetBm25);
        if (!await TryDeleteAsync(() => mChunkRepository.DeleteChunksAsync(libraryId, versionEntry.Version, ct)))
            failedTargets.Add(PurgeTargetChunks);
        if (!await TryDeleteAsync(() => mPageRepository.DeleteAsync(libraryId, versionEntry.Version, ct)))
            failedTargets.Add(PurgeTargetPages);
        if (!await TryDeleteAsync(() => mExcludedSymbolsRepository.DeleteAsync(libraryId, versionEntry.Version, ct)))
            failedTargets.Add(PurgeTargetExcludedSymbols);
        if (!await TryDeleteAsync(() => mIndexRepository.DeleteAsync(libraryId, versionEntry.Version, ct)))
            failedTargets.Add(PurgeTargetIndex);
        if (!await TryDeleteAsync(() => mProfileRepository.DeleteAsync(libraryId, versionEntry.Version, ct)))
            failedTargets.Add(PurgeTargetProfile);
        if (!await TryDeleteVersionAsync(() => mLibraryRepository.DeleteVersionAsync(libraryId, versionEntry.Version, ct)))
            failedTargets.Add(PurgeTargetVersionRecord);

        foreach (string target in failedTargets)
        {
            partialFailures.Add(new ImportPartialFailure
                                    {
                                        Version = versionEntry.Version,
                                        Reason = string.Format(PurgeFailedReasonFormat, target)
                                    });
        }

        return estimated;
    }

    private async Task UpsertLibraryRecordAsync(IBundleReader reader,
                                                 BundleManifest manifest,
                                                 IReadOnlyList<string> versionsImported,
                                                 CancellationToken ct)
    {
        // Read the full LibraryRecord from library.json to get CurrentVersion.
        var bundleLibrary = await ReadJsonAsync<LibraryRecord>(reader, BundlePaths.LibraryFile, ct);

        var existingLib = await mLibraryRepository.GetLibraryAsync(manifest.Library.Id, ct);
        var allVersions = new HashSet<string>(StringComparer.Ordinal);
        if (existingLib is not null)
            foreach (var v in existingLib.AllVersions)
                allVersions.Add(v);
        foreach (var v in versionsImported)
            allVersions.Add(v);

        // Bundle's claimed CurrentVersion wins when it is among the imported versions.
        var newCurrent = existingLib?.CurrentVersion ?? versionsImported[versionsImported.Count - 1];
        if (versionsImported.Contains(bundleLibrary.CurrentVersion))
            newCurrent = bundleLibrary.CurrentVersion;

        var updated = new LibraryRecord
                          {
                              Id = manifest.Library.Id,
                              Name = manifest.Library.Name,
                              Hint = manifest.Library.Hint,
                              CurrentVersion = newCurrent,
                              AllVersions = allVersions.OrderBy(v => v, StringComparer.Ordinal).ToList()
                          };

        await mLibraryRepository.UpsertLibraryAsync(updated, ct);
    }

    private async Task WriteVersionAsync(IBundleReader reader,
                                         string libraryId,
                                         BundleVersionEntry versionEntry,
                                         bool encoderMatches,
                                         VersionWriteLog log,
                                         CancellationToken ct)
    {
        string version = versionEntry.Version;

        // 1. Version record.
        var versionRecord = await ReadJsonAsync<LibraryVersionRecord>(
            reader, BundlePaths.VersionFilePath(version, BundlePaths.VersionFile), ct);
        await mLibraryRepository.UpsertVersionAsync(versionRecord, ct);
        log.VersionId = versionRecord.Id;

        // 2. Profile (optional).
        var profilePath = BundlePaths.VersionFilePath(version, BundlePaths.ProfileFile);
        if (versionEntry.Blobs.ContainsKey(profilePath))
        {
            var profile = await ReadJsonAsync<LibraryProfile>(reader, profilePath, ct);
            await mProfileRepository.UpsertAsync(profile, ct);
            log.ProfileId = profile.Id;
        }

        // 3. Index (optional).
        var indexPath = BundlePaths.VersionFilePath(version, BundlePaths.IndexFile);
        if (versionEntry.Blobs.ContainsKey(indexPath))
        {
            var index = await ReadJsonAsync<LibraryIndex>(reader, indexPath, ct);
            await mIndexRepository.UpsertAsync(index, ct);
            log.IndexId = index.Id;
        }

        // 4. VersionDiff (optional).
        var diffPath = BundlePaths.VersionFilePath(version, BundlePaths.VersionDiffFile);
        if (versionEntry.Blobs.ContainsKey(diffPath))
        {
            var diff = await ReadJsonAsync<VersionDiffRecord>(reader, diffPath, ct);
            await mDiffRepository.UpsertDiffAsync(diff, ct);
            log.DiffWritten = true;
        }

        // 5. ExcludedSymbols.jsonl.
        var excludedPath = BundlePaths.VersionFilePath(version, BundlePaths.ExcludedSymbolsFile);
        if (versionEntry.Blobs.ContainsKey(excludedPath))
        {
            await WriteExcludedSymbolsAsync(reader, excludedPath, log, ct);
        }

        // 6. Pages.jsonl.
        var pagesPath = BundlePaths.VersionFilePath(version, BundlePaths.PagesFile);
        if (versionEntry.Blobs.ContainsKey(pagesPath))
        {
            await WritePagesAsync(reader, pagesPath, log, ct);
        }

        // 7/8. Chunks — encoder-match attaches embeddings; mismatch leaves Embedding null.
        var chunksPath = BundlePaths.VersionFilePath(version, BundlePaths.ChunksFile);
        if (versionEntry.Blobs.ContainsKey(chunksPath))
        {
            await WriteChunksAsync(reader, version, chunksPath, versionEntry.EmbeddingDimensions,
                                   encoderMatches, log, ct);
        }

        // 9. BM25 shards — re-upload GridFS blobs then insert shards with rewritten refs.
        await ImportBm25Async(reader, libraryId, version, versionEntry, log, ct);
    }

    private async Task WriteExcludedSymbolsAsync(IBundleReader reader,
                                                  string excludedPath,
                                                  VersionWriteLog log,
                                                  CancellationToken ct)
    {
        await using var stream = reader.OpenEntry(excludedPath);
        var jsonlReader = new JsonlReader<ExcludedSymbol>(stream);
        var batch = new List<ExcludedSymbol>();
        await foreach (var symbol in jsonlReader.ReadAllAsync(ct))
        {
            batch.Add(symbol);
            log.ExcludedIds.Add(symbol.Id);
        }
        if (batch.Count > 0)
            await mExcludedSymbolsRepository.UpsertManyAsync(batch, ct);
    }

    private async Task WritePagesAsync(IBundleReader reader,
                                       string pagesPath,
                                       VersionWriteLog log,
                                       CancellationToken ct)
    {
        await using var stream = reader.OpenEntry(pagesPath);
        var jsonlReader = new JsonlReader<PageRecord>(stream);
        var batch = new List<PageRecord>(PageBatchSize);
        await foreach (var page in jsonlReader.ReadAllAsync(ct))
        {
            batch.Add(page);
            log.PageIds.Add(page.Id);
            if (batch.Count >= PageBatchSize)
            {
                await FlushPageBatchAsync(batch, ct);
            }
        }
        if (batch.Count > 0)
            await FlushPageBatchAsync(batch, ct);
    }

    private async Task FlushPageBatchAsync(List<PageRecord> batch, CancellationToken ct)
    {
        foreach (var page in batch)
            await mPageRepository.UpsertPageAsync(page, ct);
        batch.Clear();
    }

    private async Task WriteChunksAsync(IBundleReader reader,
                                         string version,
                                         string chunksPath,
                                         int dim,
                                         bool encoderMatches,
                                         VersionWriteLog log,
                                         CancellationToken ct)
    {
        // Materialize chunks from jsonl first, then attach embeddings from the blob
        // in a second pass. ZipArchive allows only one open entry at a time.
        var chunks = await MaterializeChunksAsync(reader, chunksPath, ct);

        if (encoderMatches)
            await AttachAndInsertChunksAsync(reader, version, chunks, dim, log, ct);
        else
            await InsertChunksWithNullEmbeddingsAsync(chunks, log, ct);
    }

    private static async Task<List<DocChunk>> MaterializeChunksAsync(IBundleReader reader,
                                                                       string chunksPath,
                                                                       CancellationToken ct)
    {
        await using var stream = reader.OpenEntry(chunksPath);
        var jsonlReader = new JsonlReader<DocChunk>(stream);
        var chunks = new List<DocChunk>();
        await foreach (var chunk in jsonlReader.ReadAllAsync(ct))
            chunks.Add(chunk);
        return chunks;
    }

    private async Task AttachAndInsertChunksAsync(IBundleReader reader,
                                                   string version,
                                                   List<DocChunk> chunks,
                                                   int dim,
                                                   VersionWriteLog log,
                                                   CancellationToken ct)
    {
        var embedPath = BundlePaths.VersionFilePath(version, BundlePaths.EmbeddingsBlobFile);
        await using var embedStream = reader.OpenEntry(embedPath);
        var embedReader = new EmbeddingBlobReader(embedStream, dim);

        var batch = new List<DocChunk>(ChunkBatchSize);
        foreach (var chunk in chunks)
        {
            var embedding = await embedReader.ReadAsync(ct);
            var withEmbed = chunk with { Embedding = embedding };
            batch.Add(withEmbed);
            log.ChunkIds.Add(withEmbed.Id);
            if (batch.Count >= ChunkBatchSize)
            {
                await mChunkRepository.InsertChunksAsync(batch, ct);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
            await mChunkRepository.InsertChunksAsync(batch, ct);
    }

    private async Task InsertChunksWithNullEmbeddingsAsync(List<DocChunk> chunks,
                                                            VersionWriteLog log,
                                                            CancellationToken ct)
    {
        var batch = new List<DocChunk>(ChunkBatchSize);
        foreach (var chunk in chunks)
        {
            // Encoder mismatch: store chunk with Embedding = null.
            // Task 19 will enqueue a reembed job.
            var withNull = chunk with { Embedding = null };
            batch.Add(withNull);
            log.ChunkIds.Add(withNull.Id);
            if (batch.Count >= ChunkBatchSize)
            {
                await mChunkRepository.InsertChunksAsync(batch, ct);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
            await mChunkRepository.InsertChunksAsync(batch, ct);
    }

    private async Task ImportBm25Async(IBundleReader reader,
                                        string libraryId,
                                        string version,
                                        BundleVersionEntry versionEntry,
                                        VersionWriteLog log,
                                        CancellationToken ct)
    {
        var shardsPath = BundlePaths.VersionFilePath(version, BundlePaths.Bm25ShardsFile);
        if (versionEntry.Blobs.ContainsKey(shardsPath))
        {
            var versionDirPrefix = BundlePaths.VersionDir(version) + "/" + BundlePaths.Bm25GridFsDir + "/";
            var idMap = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var entryPath in versionEntry.Blobs.Keys.Where(p =>
                         p.StartsWith(versionDirPrefix, StringComparison.Ordinal)))
            {
                var originalId = Path.GetFileNameWithoutExtension(entryPath);
                await using var src = reader.OpenEntry(entryPath);
                var newId = await mBm25Repository.UploadGridFsBlobAsync(src, ct);
                idMap[originalId] = newId;
                log.GridFsIds.Add(newId);
            }

            await using var shardsStream = reader.OpenEntry(shardsPath);
            var jsonlReader = new JsonlReader<Bm25Shard>(shardsStream);
            await foreach (var shard in jsonlReader.ReadAllAsync(ct))
            {
                var rewritten = RewriteShardRefs(shard, idMap);
                await mBm25Repository.UpsertShardAsync(rewritten, ct);
                log.ShardIds.Add(rewritten.Id);
            }
        }
    }

    private static Bm25Shard RewriteShardRefs(Bm25Shard shard, IReadOnlyDictionary<string, string> idMap)
    {
        string? rewrittenWhole = null;
        if (shard.ShardGridFsRef is not null)
        {
            if (!idMap.TryGetValue(shard.ShardGridFsRef, out var newWhole))
                throw new InvalidOperationException(
                    $"Shard references GridFS id {shard.ShardGridFsRef} not present in bundle");
            rewrittenWhole = newWhole;
        }

        var rewrittenExternal = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in shard.ExternalTerms)
        {
            if (!idMap.TryGetValue(kv.Value, out var newRef))
                throw new InvalidOperationException(
                    $"Term '{kv.Key}' references GridFS id {kv.Value} not present in bundle");
            rewrittenExternal[kv.Key] = newRef;
        }

        return shard with { ShardGridFsRef = rewrittenWhole, ExternalTerms = rewrittenExternal };
    }

    private async Task RollbackVersionAsync(VersionWriteLog log,
                                             string libraryId,
                                             string version,
                                             CancellationToken ct)
    {
        // Best-effort rollback in reverse insertion order.
        // Each step is wrapped so rollback does not mask the original failure.

        // BM25 shards and their GridFS blobs.
        if (log.ShardIds.Count > 0)
            await TryDeleteAsync(() => mBm25Repository.DeleteAsync(libraryId, version, ct));
        foreach (var gridFsId in log.GridFsIds)
            await TryDeleteBm25BlobAsync(gridFsId, ct);

        await TryDeleteAsync(() => mChunkRepository.DeleteChunksAsync(libraryId, version, ct));
        await TryDeleteAsync(() => mPageRepository.DeleteAsync(libraryId, version, ct));
        await TryDeleteAsync(() => mExcludedSymbolsRepository.DeleteAsync(libraryId, version, ct));

        // IDiffRepository has no delete method — diff is left as an orphan;
        // it has no foreign-key constraint and doesn't affect query behavior.
        _ = log.DiffWritten;

        if (log.IndexId is not null)
            await TryDeleteAsync(() => mIndexRepository.DeleteAsync(libraryId, version, ct));

        if (log.ProfileId is not null)
            await TryDeleteAsync(() => mProfileRepository.DeleteAsync(libraryId, version, ct));

        if (log.VersionId is not null)
            await TryDeleteVersionAsync(() => mLibraryRepository.DeleteVersionAsync(libraryId, version, ct));
    }

    private async Task<bool> TryDeleteBm25BlobAsync(string gridFsId, CancellationToken ct)
    {
        var res = true;
        try
        {
            await mBm25Repository.DeleteGridFsBlobAsync(gridFsId, ct);
        }
        catch
        {
            // Best-effort; swallow so rollback/purge completes as far as
            // possible. Purge callers surface the miss via the return value.
            res = false;
        }

        return res;
    }

    private static async Task<bool> TryDeleteAsync(Func<Task<long>> delete)
    {
        var res = true;
        try
        {
            await delete();
        }
        catch
        {
            // Best-effort; swallow so rollback/purge completes as far as
            // possible. Purge callers surface the miss via the return value.
            res = false;
        }

        return res;
    }

    private static async Task<bool> TryDeleteVersionAsync(Func<Task<DeleteVersionResult>> delete)
    {
        var res = true;
        try
        {
            await delete();
        }
        catch
        {
            // Best-effort; swallow so rollback/purge completes as far as
            // possible. Purge callers surface the miss via the return value.
            res = false;
        }

        return res;
    }

    private static async Task<T> ReadJsonAsync<T>(IBundleReader reader, string path, CancellationToken ct)
    {
        await using var stream = reader.OpenEntry(path);
        var result = await JsonSerializer.DeserializeAsync<T>(stream, BundleJsonOptions.Default, ct)
                     ?? throw new InvalidOperationException($"'{path}' deserialized to null");
        return result;
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

    private async Task<string> EnqueueReembedAsync(string libraryId, string version, CancellationToken ct)
    {
        var options = new ReembedOptions();
        var jobRecord = new JobRecord
                            {
                                Id = Guid.NewGuid().ToString(),
                                JobType = JobType.Reembed,
                                LibraryId = libraryId,
                                Version = version,
                                InputJson = JsonSerializer.Serialize(options),
                                Status = JobStatus.Queued,
                                ItemsLabel = ReembedItemsLabel
                            };
        await mJobRepository.UpsertAsync(jobRecord, ct);
        return jobRecord.Id;
    }

    private static string BuildRecommendedFollowUp(IReadOnlyList<string> reembedJobIds,
                                                    long bytesFreed,
                                                    bool overwroteAny)
    {
        var parts = new List<string>();
        if (reembedJobIds.Count > 0)
            parts.Add($"Re-embed in progress (jobs: {string.Join(", ", reembedJobIds)}); monitor with get_reembed_status.");
        if (overwroteAny && bytesFreed > 0)
            parts.Add($"Run compact_collections to reclaim {bytesFreed} bytes freed by overwrite.");
        return string.Join(FollowUpSeparator, parts);
    }

    #region VersionWriteLog nested class

    private sealed class VersionWriteLog
    {
        public List<string> PageIds { get; } = new();
        public List<string> ChunkIds { get; } = new();
        public string? VersionId { get; set; }
        public string? ProfileId { get; set; }
        public string? IndexId { get; set; }
        public bool DiffWritten { get; set; }
        public List<string> ExcludedIds { get; } = new();
        public List<string> ShardIds { get; } = new();
        public List<string> GridFsIds { get; } = new();
    }

    #endregion
}
