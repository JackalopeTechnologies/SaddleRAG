// LibraryRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     MongoDB implementation of library and version record data access.
/// </summary>
public class LibraryRepository : ILibraryRepository
{
    public LibraryRepository(SaddleRagDbContext context)
    {
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryRecord>> GetAllLibrariesAsync(CancellationToken ct = default)
    {
        var libraries = await mContext.Libraries
                                      .Find(FilterDefinition<LibraryRecord>.Empty)
                                      .ToListAsync(ct);
        return libraries;
    }

    /// <inheritdoc />
    public async Task<LibraryRecord?> GetLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        var result = await mContext.Libraries
                                   .Find(l => l.Id == libraryId)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task UpsertLibraryAsync(LibraryRecord library, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(library);

        await mContext.Libraries.ReplaceOneAsync(l => l.Id == library.Id,
                                                 library,
                                                 new ReplaceOptions { IsUpsert = true },
                                                 ct
                                                );
    }

    /// <inheritdoc />
    public async Task<LibraryVersionRecord?> GetVersionAsync(string libraryId,
                                                             string version,
                                                             CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var id = $"{libraryId}/{version}";
        var result = await mContext.LibraryVersions
                                   .Find(v => v.Id == id)
                                   .FirstOrDefaultAsync(ct);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LibraryVersionRecord>> GetVersionsAsync(string libraryId,
                                                                            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        var filter = Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId);
        var results = await mContext.LibraryVersions
                                    .Find(filter)
                                    .SortByDescending(v => v.ScrapedAt)
                                    .ToListAsync(ct);
        return results;
    }

    /// <inheritdoc />
    public async Task UpsertVersionAsync(LibraryVersionRecord versionRecord, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(versionRecord);

        await mContext.LibraryVersions.ReplaceOneAsync(v => v.Id == versionRecord.Id,
                                                       versionRecord,
                                                       new ReplaceOptions { IsUpsert = true },
                                                       ct
                                                      );
    }

    /// <inheritdoc />
    public async Task<DeleteVersionResult> DeleteVersionAsync(string libraryId,
                                                              string version,
                                                              CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var versionFilter =
            Builders<LibraryVersionRecord>.Filter.And(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId,
                                                               libraryId
                                                          ),
                                                      Builders<LibraryVersionRecord>.Filter.Eq(v => v.Version, version)
                                                     );
        var versionsDeleted = (await mContext.LibraryVersions.DeleteManyAsync(versionFilter, ct)).DeletedCount;

        var remaining = await mContext.LibraryVersions
                                      .Find(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId))
                                      .SortByDescending(v => v.ScrapedAt)
                                      .ToListAsync(ct);

        var libraryRowDeleted = false;
        string? repointedTo = null;

        if (remaining.Count == 0)
        {
            var libFilter = Builders<LibraryRecord>.Filter.Eq(l => l.Id, libraryId);
            var libDeleted = (await mContext.Libraries.DeleteOneAsync(libFilter, ct)).DeletedCount;
            libraryRowDeleted = libDeleted > 0;
        }
        else
        {
            var library = await GetLibraryAsync(libraryId, ct);
            if (library != null && library.CurrentVersion == version)
            {
                var newCurrent = remaining[index: 0].Version;
                library.CurrentVersion = newCurrent;
                await UpsertLibraryAsync(library, ct);
                repointedTo = newCurrent;
            }
        }

        var result = new DeleteVersionResult(versionsDeleted, libraryRowDeleted, repointedTo);
        return result;
    }

    /// <inheritdoc />
    public async Task<long> DeleteAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);

        var versions = await mContext.LibraryVersions
                                     .Find(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId))
                                     .ToListAsync(ct);

        long total = 0;
        foreach(var v in versions)
        {
            var result = await DeleteVersionAsync(libraryId, v.Version, ct);
            total += result.VersionsDeleted;
        }

        var libFilter = Builders<LibraryRecord>.Filter.Eq(l => l.Id, libraryId);
        await mContext.Libraries.DeleteOneAsync(libFilter, ct);

        return total;
    }

    /// <inheritdoc />
    public async Task<RenameLibraryResponse> RenameAsync(string oldId, string newId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldId);
        ArgumentException.ThrowIfNullOrEmpty(newId);

        RenameLibraryResponse result;

        var existing = await GetLibraryAsync(oldId, ct);
        if (existing == null)
            result = new RenameLibraryResponse(RenameLibraryOutcome.NotFound, Counts: null);
        else
        {
            var collision = await GetLibraryAsync(newId, ct);
            if (collision != null)
                result = new RenameLibraryResponse(RenameLibraryOutcome.Collision, Counts: null);
            else
            {
                var counts = await ApplyRenameAsync(oldId, newId, ct);
                result = new RenameLibraryResponse(RenameLibraryOutcome.Renamed, counts);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SetSuspectAsync(string libraryId,
                                      string version,
                                      IReadOnlyList<string> reasons,
                                      CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(reasons);

        var filter =
            Builders<LibraryVersionRecord>.Filter.And(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId,
                                                               libraryId
                                                          ),
                                                      Builders<LibraryVersionRecord>.Filter.Eq(v => v.Version, version)
                                                     );
        var update = Builders<LibraryVersionRecord>.Update
                                                   .Set(v => v.Suspect, value: true)
                                                   .Set(v => v.SuspectReasons, reasons)
                                                   .Set(v => v.LastSuspectEvaluatedAt, DateTime.UtcNow);
        await mContext.LibraryVersions.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task ClearSuspectAsync(string libraryId, string version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);

        var filter =
            Builders<LibraryVersionRecord>.Filter.And(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId,
                                                               libraryId
                                                          ),
                                                      Builders<LibraryVersionRecord>.Filter.Eq(v => v.Version, version)
                                                     );
        var update = Builders<LibraryVersionRecord>.Update
                                                   .Set(v => v.Suspect, value: false)
                                                   .Set(v => v.SuspectReasons, [])
                                                   .Set(v => v.LastSuspectEvaluatedAt, DateTime.UtcNow);
        await mContext.LibraryVersions.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    private const int RemapBatchSize = 500;

    private static string RemapIdSegment(string id, int segmentIndex, string newSegment)
    {
        var segments = id.Split('/');
        segments[segmentIndex] = newSegment;
        return string.Join('/', segments);
    }

    private static async Task<long> CopyRemappedAsync<T>(IMongoCollection<T> collection,
                                                  FilterDefinition<T> oldFilter,
                                                  Func<T, T> rebuild,
                                                  CancellationToken ct)
    {
        long copied = 0;
        var batch = new List<T>(RemapBatchSize);
        using var cursor = await collection.FindAsync(oldFilter, cancellationToken: ct);
        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var doc in cursor.Current)
            {
                batch.Add(rebuild(doc));
                if (batch.Count >= RemapBatchSize)
                {
                    await collection.InsertManyAsync(batch, cancellationToken: ct);
                    copied += batch.Count;
                    batch.Clear();
                }
            }
        }

        if (batch.Count > 0)
        {
            await collection.InsertManyAsync(batch, cancellationToken: ct);
            copied += batch.Count;
        }

        return copied;
    }

    private async Task<RenameLibraryResult> ApplyRenameAsync(string oldId, string newId, CancellationToken ct)
    {
        // Copy phase: every (LibraryId)-keyed collection. _id embeds the library id
        // (segment 0) and is immutable, so each row is re-inserted under a rebuilt _id;
        // old rows are deleted afterwards.
        var versions = await CopyRemappedAsync(mContext.LibraryVersions,
            Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, oldId),
            d => d with { Id = RemapIdSegment(d.Id, 0, newId), LibraryId = newId }, ct);

        var chunks = await CopyRemappedAsync(mContext.Chunks,
            Builders<DocChunk>.Filter.Eq(c => c.LibraryId, oldId),
            d => d with { Id = RemapIdSegment(d.Id, 0, newId), LibraryId = newId }, ct);

        var pages = await CopyRemappedAsync(mContext.Pages,
            Builders<PageRecord>.Filter.Eq(p => p.LibraryId, oldId),
            d => d with { Id = RemapIdSegment(d.Id, 0, newId), LibraryId = newId }, ct);

        var profiles = await CopyRemappedAsync(mContext.LibraryProfiles,
            Builders<LibraryProfile>.Filter.Eq(p => p.LibraryId, oldId),
            d => d with { Id = RemapIdSegment(d.Id, 0, newId), LibraryId = newId }, ct);

        var indexes = await CopyRemappedAsync(mContext.LibraryIndexes,
            Builders<LibraryIndex>.Filter.Eq(i => i.LibraryId, oldId),
            d => d with { Id = RemapIdSegment(d.Id, 0, newId), LibraryId = newId }, ct);

        var shards = await CopyRemappedAsync(mContext.Bm25Shards,
            Builders<Bm25Shard>.Filter.Eq(s => s.LibraryId, oldId),
            d => d with { Id = RemapIdSegment(d.Id, 0, newId), LibraryId = newId }, ct);

        var excluded = await CopyRemappedAsync(mContext.ExcludedSymbols,
            Builders<ExcludedSymbol>.Filter.Eq(e => e.LibraryId, oldId),
            d => d with { Id = RemapIdSegment(d.Id, 0, newId), LibraryId = newId }, ct);

        // Jobs: GUID _id — a field update is sufficient.
        var jobFilter = Builders<JobRecord>.Filter.Eq(j => j.LibraryId, oldId);
        var jobUpdate = Builders<JobRecord>.Update.Set(j => j.LibraryId, newId);
        var jobRes = await mContext.Jobs.UpdateManyAsync(jobFilter, jobUpdate, cancellationToken: ct);

        // Pointer flip: the libraries row _id IS the library id, so move it (insert new, delete old).
        var oldLib = await GetLibraryAsync(oldId, ct);
        if (oldLib == null)
            throw new InvalidOperationException($"Library '{oldId}' disappeared during rename.");
        var newLib = new LibraryRecord
                         {
                             Id = newId,
                             Name = oldLib.Name,
                             Hint = oldLib.Hint,
                             CurrentVersion = oldLib.CurrentVersion,
                             AllVersions = oldLib.AllVersions
                         };
        await mContext.Libraries.InsertOneAsync(newLib, cancellationToken: ct);

        // Delete phase: old rows now that copies exist.
        await mContext.LibraryVersions.DeleteManyAsync(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, oldId), ct);
        await mContext.Chunks.DeleteManyAsync(Builders<DocChunk>.Filter.Eq(c => c.LibraryId, oldId), ct);
        await mContext.Pages.DeleteManyAsync(Builders<PageRecord>.Filter.Eq(p => p.LibraryId, oldId), ct);
        await mContext.LibraryProfiles.DeleteManyAsync(Builders<LibraryProfile>.Filter.Eq(p => p.LibraryId, oldId), ct);
        await mContext.LibraryIndexes.DeleteManyAsync(Builders<LibraryIndex>.Filter.Eq(i => i.LibraryId, oldId), ct);
        await mContext.Bm25Shards.DeleteManyAsync(Builders<Bm25Shard>.Filter.Eq(s => s.LibraryId, oldId), ct);
        await mContext.ExcludedSymbols.DeleteManyAsync(Builders<ExcludedSymbol>.Filter.Eq(e => e.LibraryId, oldId), ct);
        await mContext.Libraries.DeleteOneAsync(l => l.Id == oldId, ct);

        var result = new RenameLibraryResult(Libraries: 1, versions, chunks, pages, profiles, indexes, shards, excluded,
                                             jobRes.ModifiedCount);
        return result;
    }
}
