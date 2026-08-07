// LibraryRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using MongoDB.Driver;
using SaddleRAG.Core.Enums;
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
    public async Task<IReadOnlyList<LibraryVersionRecord>> GetVersionsByPublicationStateAsync(
        VersionPublicationState publicationState,
        CancellationToken ct = default)
    {
        var stateFilter = Builders<LibraryVersionRecord>.Filter.Eq(v => v.PublicationState, publicationState);
        FilterDefinition<LibraryVersionRecord> filter = stateFilter;
        if (publicationState == VersionPublicationState.Published)
        {
            var legacyFilter = Builders<LibraryVersionRecord>.Filter.Exists(nameof(LibraryVersionRecord.PublicationState),
                                                                             exists: false);
            filter = Builders<LibraryVersionRecord>.Filter.Or(stateFilter, legacyFilter);
        }

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
    public async Task<DirectoryVersionClaimResult> TryClaimDirectoryVersionAsync(
        LibraryVersionRecord buildingVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buildingVersion);
        ValidateDirectoryVersion(buildingVersion, VersionPublicationState.Building);

        var claimableFilter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.Id, buildingVersion.Id),
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.PublicationState,
                                                       VersionPublicationState.Failed));
        var options = new FindOneAndReplaceOptions<LibraryVersionRecord>
                          {
                              IsUpsert = true,
                              ReturnDocument = ReturnDocument.Before
                          };
        DirectoryVersionClaimResult? result = null;
        while(result == null)
        {
            try
            {
                LibraryVersionRecord? predecessor = await mContext.LibraryVersions.FindOneAndReplaceAsync(
                                                        claimableFilter,
                                                        buildingVersion,
                                                        options,
                                                        ct);
                result = new DirectoryVersionClaimResult(DirectoryVersionClaimStatus.Acquired,
                                                         RequiresCleanup: predecessor != null);
            }
            catch(MongoException ex) when (IsDuplicateKey(ex))
            {
                LibraryVersionRecord? current = await mContext.LibraryVersions.Find(version =>
                                                          version.Id == buildingVersion.Id)
                                                      .FirstOrDefaultAsync(ct);
                result = ExistingClaimResult(current);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryPublishDirectoryVersionAsync(LibraryVersionRecord publishedVersion,
                                                            string scanRunId,
                                                            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publishedVersion);
        ValidateDirectoryVersion(publishedVersion, VersionPublicationState.Published);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        if (!scanRunId.Equals(publishedVersion.ScanRunId, StringComparison.Ordinal))
            throw new ArgumentException("The scan owner must match the published version.", nameof(scanRunId));

        var filter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.Id, publishedVersion.Id),
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.PublicationState,
                                                       VersionPublicationState.Building),
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.ScanRunId, scanRunId),
            Builders<LibraryVersionRecord>.Filter.Ne(version => version.CleanupInProgress, value: true));
        ReplaceOneResult replacement = await mContext.LibraryVersions.ReplaceOneAsync(filter,
                                                                                        publishedVersion,
                                                                                        cancellationToken: ct);
        bool result = replacement.MatchedCount == 1;
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryBeginDirectoryVersionCleanupAsync(string libraryId,
                                                                 string version,
                                                                 string scanRunId,
                                                                 CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);

        var ownedState = Builders<LibraryVersionRecord>.Filter.Or(
            Builders<LibraryVersionRecord>.Filter.Eq(item => item.PublicationState,
                                                      VersionPublicationState.Building),
            Builders<LibraryVersionRecord>.Filter.Eq(item => item.PublicationState,
                                                      VersionPublicationState.Published));
        var filter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(item => item.LibraryId, libraryId),
            Builders<LibraryVersionRecord>.Filter.Eq(item => item.Version, version),
            Builders<LibraryVersionRecord>.Filter.Eq(item => item.ScanRunId, scanRunId),
            Builders<LibraryVersionRecord>.Filter.Ne(item => item.CleanupInProgress, value: true),
            ownedState);
        var update = Builders<LibraryVersionRecord>.Update.Set(item => item.CleanupInProgress, value: true);
        UpdateResult updated = await mContext.LibraryVersions.UpdateOneAsync(filter,
                                                                             update,
                                                                             cancellationToken: ct);
        bool result = updated.MatchedCount == 1;
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryRecordDirectoryVersionFailureAsync(LibraryVersionRecord failedVersion,
                                                                  string scanRunId,
                                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failedVersion);
        ValidateDirectoryVersion(failedVersion, VersionPublicationState.Failed);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        if (!scanRunId.Equals(failedVersion.ScanRunId, StringComparison.Ordinal))
            throw new ArgumentException("The scan owner must match the failed version.", nameof(scanRunId));

        var filter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.Id, failedVersion.Id),
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.ScanRunId, scanRunId),
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.CleanupInProgress, value: true));
        var options = new FindOneAndReplaceOptions<LibraryVersionRecord>
                          {
                              IsUpsert = true,
                              ReturnDocument = ReturnDocument.Before
                          };
        bool result;
        try
        {
            await mContext.LibraryVersions.FindOneAndReplaceAsync(filter,
                                                                  failedVersion,
                                                                  options,
                                                                  ct);
            result = true;
        }
        catch(MongoException ex) when (IsDuplicateKey(ex))
        {
            result = false;
        }

        return result;
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
        var publishedRemaining = remaining.Where(v => v.PublicationState == VersionPublicationState.Published)
                                          .ToList();

        var libraryRowDeleted = false;
        string? repointedTo = null;

        if (publishedRemaining.Count == 0)
        {
            var libFilter = Builders<LibraryRecord>.Filter.Eq(l => l.Id, libraryId);
            var libDeleted = (await mContext.Libraries.DeleteOneAsync(libFilter, ct)).DeletedCount;
            libraryRowDeleted = libDeleted > 0;
        }
        else
        {
            var library = await GetLibraryAsync(libraryId, ct);
            if (library != null)
            {
                library.AllVersions.RemoveAll(v => string.Equals(v, version, StringComparison.Ordinal));
                var current = remaining.FirstOrDefault(v => v.Version == library.CurrentVersion);
                if (publishedRemaining.Count > 0 &&
                    (library.CurrentVersion == version ||
                     current?.PublicationState != VersionPublicationState.Published))
                {
                    var newCurrent = publishedRemaining[index: 0].Version;
                    library.CurrentVersion = newCurrent;
                    repointedTo = newCurrent;
                }

                await UpsertLibraryAsync(library, ct);
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

    /// <inheritdoc />
    public async Task<RenameLibraryResponse> RenameVersionAsync(string libraryId,
                                                                string oldVersion,
                                                                string newVersion,
                                                                CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(oldVersion);
        ArgumentException.ThrowIfNullOrEmpty(newVersion);

        RenameLibraryResponse result;

        var existing = await GetVersionAsync(libraryId, oldVersion, ct);
        if (existing == null)
            result = new RenameLibraryResponse(RenameLibraryOutcome.NotFound, Counts: null);
        else
        {
            var collision = await GetVersionAsync(libraryId, newVersion, ct);
            if (collision != null)
                result = new RenameLibraryResponse(RenameLibraryOutcome.Collision, Counts: null);
            else
            {
                var counts = await ApplyVersionRenameAsync(libraryId, oldVersion, newVersion, ct);
                result = new RenameLibraryResponse(RenameLibraryOutcome.Renamed, counts);
            }
        }

        return result;
    }

    private async Task<RenameLibraryResult> ApplyVersionRenameAsync(string lib,
                                                                     string oldVer,
                                                                     string newVer,
                                                                     CancellationToken ct)
    {
        static FilterDefinition<T> ByLibVer<T>(string l, string v) =>
            Builders<T>.Filter.And(
                Builders<T>.Filter.Eq(new StringFieldDefinition<T, string>(LibraryIdField), l),
                Builders<T>.Filter.Eq(new StringFieldDefinition<T, string>(VersionField), v));

        // Copy phase: version is segment 1 of every composite _id.
        var versions = await CopyRemappedAsync(mContext.LibraryVersions, ByLibVer<LibraryVersionRecord>(lib, oldVer),
            d => d with
                     {
                         Id = RemapIdSegment(d.Id, 1, newVer),
                         Version = newVer,
                         PreviousVersion = string.Equals(d.PreviousVersion, oldVer, StringComparison.Ordinal)
                                               ? newVer
                                               : d.PreviousVersion
                     }, ct);

        var chunks = await CopyRemappedAsync(mContext.Chunks, ByLibVer<DocChunk>(lib, oldVer),
            d => LibraryRenameMapper.MapChunk(d, lib, newVer), ct);

        var pages = await CopyRemappedAsync(mContext.Pages, ByLibVer<PageRecord>(lib, oldVer),
            d => LibraryRenameMapper.MapPage(d, lib, newVer), ct);

        _ = await CopyRemappedAsync(mContext.DocumentRevisions,
                                    ByLibVer<DocumentRevisionRecord>(lib, oldVer),
                                    d => LibraryRenameMapper.MapDocumentRevision(d, lib, newVer),
                                    ct);

        _ = await CopyRemappedAsync(mContext.SubjectAssignments,
                                    ByLibVer<SubjectAssignmentRecord>(lib, oldVer),
                                    d => LibraryRenameMapper.MapSubjectAssignment(d, lib, newVer),
                                    ct);

        _ = await ReplaceRemappedAsync(mContext.SourceDocuments,
                                       Builders<SourceDocumentRecord>.Filter.Eq(d => d.LibraryId, lib),
                                       d => d.Id,
                                       d => LibraryRenameMapper.MapSourceDocument(d, lib, oldVer, newVer),
                                       ct);

        _ = await ReplaceRemappedAsync(mContext.DirectoryLibraries,
                                       Builders<DirectoryLibraryDefinition>.Filter.Eq(d => d.Id, lib),
                                       d => d.Id,
                                       d => LibraryRenameMapper.MapDirectoryDefinition(d, lib, oldVer, newVer),
                                       ct);

        var profiles = await CopyRemappedAsync(mContext.LibraryProfiles, ByLibVer<LibraryProfile>(lib, oldVer),
            d => d with { Id = RemapIdSegment(d.Id, 1, newVer), Version = newVer }, ct);

        var indexes = await CopyRemappedAsync(mContext.LibraryIndexes, ByLibVer<LibraryIndex>(lib, oldVer),
            d => d with { Id = RemapIdSegment(d.Id, 1, newVer), Version = newVer }, ct);

        // Bm25 shards keep their GridFS blob refs (ShardGridFsRef / ExternalTerms) unchanged.
        var shards = await CopyRemappedAsync(mContext.Bm25Shards, ByLibVer<Bm25Shard>(lib, oldVer),
            d => d with { Id = RemapIdSegment(d.Id, 1, newVer), Version = newVer }, ct);

        var excluded = await CopyRemappedAsync(mContext.ExcludedSymbols, ByLibVer<ExcludedSymbol>(lib, oldVer),
            d => d with { Id = RemapIdSegment(d.Id, 1, newVer), Version = newVer }, ct);

        // Jobs: GUID _id — field update.
        var jobUpdate = Builders<JobRecord>.Update.Set(j => j.Version, newVer);
        var jobRes = await mContext.Jobs.UpdateManyAsync(ByLibVer<JobRecord>(lib, oldVer), jobUpdate, cancellationToken: ct);

        // Pointer flip: libraries _id is unchanged; update AllVersions / CurrentVersion fields.
        var libRec = await GetLibraryAsync(lib, ct);
        if (libRec == null)
            throw new InvalidOperationException($"Library '{lib}' disappeared during version rename.");
        var newAll = libRec.AllVersions.Select(v => v == oldVer ? newVer : v).ToList();
        var newCurrent = libRec.CurrentVersion == oldVer ? newVer : libRec.CurrentVersion;
        await UpsertLibraryAsync(new LibraryRecord
                                     {
                                         Id = libRec.Id, Name = libRec.Name, Hint = libRec.Hint,
                                         CurrentVersion = newCurrent, AllVersions = newAll
                                     }, ct);

        // Delete phase: old version rows.
        await mContext.LibraryVersions.DeleteManyAsync(ByLibVer<LibraryVersionRecord>(lib, oldVer), ct);
        await mContext.Chunks.DeleteManyAsync(ByLibVer<DocChunk>(lib, oldVer), ct);
        await mContext.Pages.DeleteManyAsync(ByLibVer<PageRecord>(lib, oldVer), ct);
        await mContext.LibraryProfiles.DeleteManyAsync(ByLibVer<LibraryProfile>(lib, oldVer), ct);
        await mContext.LibraryIndexes.DeleteManyAsync(ByLibVer<LibraryIndex>(lib, oldVer), ct);
        await mContext.Bm25Shards.DeleteManyAsync(ByLibVer<Bm25Shard>(lib, oldVer), ct);
        await mContext.ExcludedSymbols.DeleteManyAsync(ByLibVer<ExcludedSymbol>(lib, oldVer), ct);
        await mContext.DocumentRevisions.DeleteManyAsync(ByLibVer<DocumentRevisionRecord>(lib, oldVer), ct);
        await mContext.SubjectAssignments.DeleteManyAsync(ByLibVer<SubjectAssignmentRecord>(lib, oldVer), ct);

        var result = new RenameLibraryResult(Libraries: 1, versions, chunks, pages, profiles, indexes, shards, excluded,
                                             jobRes.ModifiedCount);
        return result;
    }

    private const int RemapBatchSize = 500;
    private const string LibraryIdField = "LibraryId";
    private const string VersionField = "Version";
    private const string MongoIdField = "_id";

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

    private static async Task<long> ReplaceRemappedAsync<T>(IMongoCollection<T> collection,
                                                             FilterDefinition<T> filter,
                                                             Func<T, string> idSelector,
                                                             Func<T, T> rebuild,
                                                             CancellationToken ct)
    {
        long replaced = 0;
        using var cursor = await collection.FindAsync(filter, cancellationToken: ct);
        while(await cursor.MoveNextAsync(ct))
        {
            foreach(T document in cursor.Current)
            {
                string id = idSelector(document);
                T replacement = rebuild(document);
                FilterDefinition<T> idFilter = Builders<T>.Filter.Eq(
                    new StringFieldDefinition<T, string>(MongoIdField),
                    id);
                ReplaceOneResult result = await collection.ReplaceOneAsync(idFilter,
                                                                             replacement,
                                                                             cancellationToken: ct);
                replaced += result.ModifiedCount;
            }
        }

        return replaced;
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
            d => LibraryRenameMapper.MapChunk(d, newId, d.Version), ct);

        var pages = await CopyRemappedAsync(mContext.Pages,
            Builders<PageRecord>.Filter.Eq(p => p.LibraryId, oldId),
            d => LibraryRenameMapper.MapPage(d, newId, d.Version), ct);

        _ = await CopyRemappedAsync(mContext.DirectoryLibraries,
                                    Builders<DirectoryLibraryDefinition>.Filter.Eq(d => d.Id, oldId),
                                    d => LibraryRenameMapper.MapDirectoryDefinition(d, newId),
                                    ct);

        _ = await ReplaceRemappedAsync(mContext.SourceDocuments,
                                       Builders<SourceDocumentRecord>.Filter.Eq(d => d.LibraryId, oldId),
                                       d => d.Id,
                                       d => LibraryRenameMapper.MapSourceDocument(d, newId),
                                       ct);

        _ = await CopyRemappedAsync(mContext.DocumentRevisions,
                                    Builders<DocumentRevisionRecord>.Filter.Eq(d => d.LibraryId, oldId),
                                    d => LibraryRenameMapper.MapDocumentRevision(d, newId, d.Version),
                                    ct);

        _ = await CopyRemappedAsync(mContext.SubjectCatalogs,
                                    Builders<SubjectCatalogRecord>.Filter.Eq(d => d.LibraryId, oldId),
                                    d => LibraryRenameMapper.MapSubjectCatalog(d, newId),
                                    ct);

        _ = await CopyRemappedAsync(mContext.SubjectAssignments,
                                    Builders<SubjectAssignmentRecord>.Filter.Eq(d => d.LibraryId, oldId),
                                    d => LibraryRenameMapper.MapSubjectAssignment(d, newId, d.Version),
                                    ct);

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
        await mContext.DirectoryLibraries.DeleteManyAsync(
            Builders<DirectoryLibraryDefinition>.Filter.Eq(d => d.Id, oldId),
            ct);
        await mContext.DocumentRevisions.DeleteManyAsync(
            Builders<DocumentRevisionRecord>.Filter.Eq(d => d.LibraryId, oldId),
            ct);
        await mContext.SubjectCatalogs.DeleteManyAsync(
            Builders<SubjectCatalogRecord>.Filter.Eq(d => d.LibraryId, oldId),
            ct);
        await mContext.SubjectAssignments.DeleteManyAsync(
            Builders<SubjectAssignmentRecord>.Filter.Eq(d => d.LibraryId, oldId),
            ct);
        await mContext.Libraries.DeleteOneAsync(l => l.Id == oldId, ct);

        var result = new RenameLibraryResult(Libraries: 1, versions, chunks, pages, profiles, indexes, shards, excluded,
                                             jobRes.ModifiedCount);
        return result;
    }

    private static DirectoryVersionClaimResult? ExistingClaimResult(LibraryVersionRecord? current)
    {
        DirectoryVersionClaimResult? result = current?.PublicationState switch
                                                  {
                                                      VersionPublicationState.Published =>
                                                          new DirectoryVersionClaimResult(
                                                              DirectoryVersionClaimStatus.AlreadyPublished),
                                                      VersionPublicationState.Building =>
                                                          new DirectoryVersionClaimResult(
                                                              DirectoryVersionClaimStatus.InProgress),
                                                      VersionPublicationState.Failed => null,
                                                      _ => new DirectoryVersionClaimResult(
                                                          DirectoryVersionClaimStatus.InProgress)
                                                  };
        return result;
    }

    private static bool IsDuplicateKey(MongoException exception)
    {
        bool result = exception switch
                          {
                              MongoWriteException writeException =>
                                  writeException.WriteError?.Category == ServerErrorCategory.DuplicateKey,
                              MongoCommandException commandException => commandException.Code == DuplicateKeyErrorCode,
                              _ => false
                          };
        return result;
    }

    private static void ValidateDirectoryVersion(LibraryVersionRecord version,
                                                 VersionPublicationState expectedState)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrEmpty(version.Id);
        ArgumentException.ThrowIfNullOrEmpty(version.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(version.Version);
        ArgumentException.ThrowIfNullOrEmpty(version.ScanRunId);
        if (version.PublicationState != expectedState)
            throw new ArgumentException($"Directory version must be {expectedState}.", nameof(version));
        if (version.CleanupInProgress)
            throw new ArgumentException("A replacement directory version cannot already be in cleanup.",
                                        nameof(version));
    }

    private const int DuplicateKeyErrorCode = 11000;
}
