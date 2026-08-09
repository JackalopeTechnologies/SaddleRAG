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
    public async Task<bool> TryReplaceLibrarySummaryAsync(LibraryRecord expected,
                                                          LibraryRecord replacement,
                                                          CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(expected.Id, replacement.Id, StringComparison.Ordinal))
            throw new ArgumentException("Replacement library id must match the expected summary.",
                                        nameof(replacement));

        ReplaceOneResult result = await mContext.Libraries.ReplaceOneAsync(ExactLibrarySummaryFilter(expected),
                                                                            replacement,
                                                                            cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryDeleteLibrarySummaryAsync(LibraryRecord expected,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        DeleteResult result = await mContext.Libraries.DeleteOneAsync(ExactLibrarySummaryFilter(expected), ct);
        return result.DeletedCount == 1;
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
    public async Task<bool> TryClaimImportVersionAsync(LibraryVersionRecord buildingVersion,
                                                       string importOperationId,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buildingVersion);
        ArgumentException.ThrowIfNullOrEmpty(importOperationId);
        ValidateImportVersion(buildingVersion, importOperationId, VersionPublicationState.Building);

        bool result;
        try
        {
            await mContext.LibraryVersions.InsertOneAsync(buildingVersion, cancellationToken: ct);
            result = true;
        }
        catch(MongoException ex) when (IsDuplicateKey(ex))
        {
            result = false;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryPublishImportVersionAsync(LibraryVersionRecord publishedVersion,
                                                         string importOperationId,
                                                         CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publishedVersion);
        ArgumentException.ThrowIfNullOrEmpty(importOperationId);
        ValidateImportVersion(publishedVersion, importOperationId, VersionPublicationState.Published);

        var filter = Builders<LibraryVersionRecord>.Filter.And(
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.Id, publishedVersion.Id),
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.PublicationState,
                                                       VersionPublicationState.Building),
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.ImportOperationId, importOperationId),
            Builders<LibraryVersionRecord>.Filter.Ne(version => version.CleanupInProgress, value: true));
        ReplaceOneResult replacement = await mContext.LibraryVersions.ReplaceOneAsync(filter,
                                                                                        publishedVersion,
                                                                                        cancellationToken: ct);
        return replacement.MatchedCount == 1;
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
            Builders<LibraryVersionRecord>.Filter.Eq(version => version.RegistrationRevision,
                                                       publishedVersion.RegistrationRevision),
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

        IReadOnlyList<LibraryVersionRecord> existing = await mContext.LibraryVersions
                                                                     .Find(candidate =>
                                                                         candidate.LibraryId == libraryId)
                                                                     .ToListAsync(ct);
        LibraryVersionRecord? target = existing.FirstOrDefault(candidate =>
                                                     string.Equals(candidate.Version,
                                                                   version,
                                                                   StringComparison.Ordinal));

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

        var versions = (await mContext.LibraryVersions
                                      .Find(Builders<LibraryVersionRecord>.Filter.Eq(v => v.LibraryId, libraryId))
                                      .ToListAsync(ct))
                       .OrderBy(version => version.PublicationState == VersionPublicationState.Published ? 1 : 0)
                       .ToList();

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
    public Task<RenameLibraryResponse> RenameAsync(string oldId,
                                                   string newId,
                                                   CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldId);
        ArgumentException.ThrowIfNullOrEmpty(newId);
        throw new InvalidOperationException(UnsafeRenameMessage);
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
    public Task<RenameLibraryResponse> RenameVersionAsync(string libraryId,
                                                          string oldVersion,
                                                          string newVersion,
                                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(oldVersion);
        ArgumentException.ThrowIfNullOrEmpty(newVersion);
        throw new InvalidOperationException(UnsafeRenameMessage);
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
        if (version.RegistrationRevision == null || version.RegistrationRevision < 0)
            throw new ArgumentException("A directory version requires a valid registration revision.",
                                        nameof(version));
        if (version.PublicationState != expectedState)
            throw new ArgumentException($"Directory version must be {expectedState}.", nameof(version));
        if (version.CleanupInProgress)
            throw new ArgumentException("A replacement directory version cannot already be in cleanup.",
                                        nameof(version));
    }

    private static void ValidateImportVersion(LibraryVersionRecord version,
                                              string importOperationId,
                                              VersionPublicationState expectedState)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentException.ThrowIfNullOrEmpty(version.Id);
        ArgumentException.ThrowIfNullOrEmpty(version.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(version.Version);
        ArgumentException.ThrowIfNullOrEmpty(importOperationId);
        if (!string.Equals(version.ImportOperationId, importOperationId, StringComparison.Ordinal))
            throw new ArgumentException("The import owner must match the version record.", nameof(importOperationId));
        if (version.PublicationState != expectedState)
            throw new ArgumentException($"Import version must be {expectedState}.", nameof(version));
        if (version.CleanupInProgress)
            throw new ArgumentException("An import version cannot already be in cleanup.", nameof(version));
    }

    private static FilterDefinition<LibraryRecord> ExactLibrarySummaryFilter(LibraryRecord expected) =>
        Builders<LibraryRecord>.Filter.And(
            Builders<LibraryRecord>.Filter.Eq(library => library.Id, expected.Id),
            Builders<LibraryRecord>.Filter.Eq(library => library.Name, expected.Name),
            Builders<LibraryRecord>.Filter.Eq(library => library.Hint, expected.Hint),
            Builders<LibraryRecord>.Filter.Eq(library => library.CurrentVersion, expected.CurrentVersion),
            Builders<LibraryRecord>.Filter.Eq(library => library.AllVersions, expected.AllVersions));

    private const int DuplicateKeyErrorCode = 11000;
    private const string UnsafeRenameMessage =
        "Direct repository renames are unsafe. Use ILibraryRenameService so durable mode fencing and recovery remain active.";
}
