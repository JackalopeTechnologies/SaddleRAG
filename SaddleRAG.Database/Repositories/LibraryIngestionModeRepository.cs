// LibraryIngestionModeRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Database.Repositories;

/// <summary>MongoDB implementation of the cross-process library source-mode fence.</summary>
public sealed class LibraryIngestionModeRepository : ILibraryIngestionModeRepository
{
    public LibraryIngestionModeRepository(SaddleRagDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        mContext = context;
    }

    private readonly SaddleRagDbContext mContext;

    public async Task<LibraryIngestionModeRecord?> TryAcquireAsync(string libraryId,
                                                                   LibraryIngestionMode mode,
                                                                   string ownerToken,
                                                                   DateTime nowUtc,
                                                                   DateTime expiresAtUtc,
                                                                   CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        ValidateLeasePeriod(nowUtc, expiresAtUtc);
        bool renameInProgress = await ExistsAsync(mContext.LibraryRenameOperations,
                                                   operation => operation.SourceLibraryId == libraryId ||
                                                                operation.TargetLibraryId == libraryId,
                                                   ct);
        LibraryIngestionModeRecord? result = null;
        if (!renameInProgress)
        {
            var filter = Builders<LibraryIngestionModeRecord>.Filter.And(
                Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.Id, libraryId),
                Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.Mode, mode),
                Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.PendingRenameOperationId, null),
                Builders<LibraryIngestionModeRecord>.Filter.Or(
                    Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.LeaseOwnerToken, null),
                    Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.LeaseOwnerToken, ownerToken),
                    Builders<LibraryIngestionModeRecord>.Filter.Lte(record => record.LeaseExpiresAtUtc, nowUtc)));
            var update = Builders<LibraryIngestionModeRecord>.Update
                         .SetOnInsert(record => record.Id, libraryId)
                         .SetOnInsert(record => record.Mode, mode)
                         .SetOnInsert(record => record.OwnershipState, LibraryIngestionOwnershipState.Reserved)
                         .SetOnInsert(record => record.ReservedAtUtc, nowUtc)
                         .Set(record => record.LeaseOwnerToken, ownerToken)
                         .Set(record => record.LeaseExpiresAtUtc, expiresAtUtc)
                         .Set(record => record.UpdatedAtUtc, nowUtc);
            var options = new FindOneAndUpdateOptions<LibraryIngestionModeRecord>
                              {
                                  IsUpsert = true,
                                  ReturnDocument = ReturnDocument.After
                              };
            try
            {
                result = await mContext.LibraryIngestionModes.FindOneAndUpdateAsync(filter, update, options, ct);
            }
            catch(MongoException ex) when(IsDuplicateKey(ex))
            {
                result = null;
            }
        }

        return result;
    }

    public async Task<LibraryIngestionModeRecord?> TryAcquireRenameRecoveryAsync(
        string libraryId,
        LibraryIngestionMode mode,
        string renameOperationId,
        string ownerToken,
        DateTime nowUtc,
        DateTime expiresAtUtc,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        ArgumentException.ThrowIfNullOrEmpty(renameOperationId);
        ValidateLeasePeriod(nowUtc, expiresAtUtc);
        LibraryRenameOperationRecord? operation = await mContext.LibraryRenameOperations
                                                                  .Find(candidate =>
                                                                      candidate.OperationId == renameOperationId &&
                                                                      (candidate.SourceLibraryId == libraryId ||
                                                                       candidate.TargetLibraryId == libraryId))
                                                                  .FirstOrDefaultAsync(ct);
        LibraryIngestionModeRecord? result = null;
        if (operation != null && operation.Mode == mode)
        {
            FilterDefinition<LibraryIngestionModeRecord> pendingFilter =
                Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.PendingRenameOperationId,
                                                                renameOperationId);
            if (operation.State is LibraryRenameOperationState.Applying or
                LibraryRenameOperationState.VectorCommitted)
            {
                pendingFilter |= Builders<LibraryIngestionModeRecord>.Filter.Eq(
                    record => record.PendingRenameOperationId,
                    value: null);
            }

            var filter = Builders<LibraryIngestionModeRecord>.Filter.And(
                Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.Id, libraryId),
                Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.Mode, mode),
                pendingFilter,
                Builders<LibraryIngestionModeRecord>.Filter.Or(
                    Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.LeaseOwnerToken, null),
                    Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.LeaseOwnerToken, ownerToken),
                    Builders<LibraryIngestionModeRecord>.Filter.Lte(record => record.LeaseExpiresAtUtc, nowUtc)));
            var update = Builders<LibraryIngestionModeRecord>.Update
                         .Set(record => record.LeaseOwnerToken, ownerToken)
                         .Set(record => record.LeaseExpiresAtUtc, expiresAtUtc)
                         .Set(record => record.UpdatedAtUtc, nowUtc);
            var options = new FindOneAndUpdateOptions<LibraryIngestionModeRecord>
                              {
                                  IsUpsert = false,
                                  ReturnDocument = ReturnDocument.After
                              };
            result = await mContext.LibraryIngestionModes.FindOneAndUpdateAsync(filter,
                                                                                 update,
                                                                                 options,
                                                                                 ct);
        }

        return result;
    }

    public async Task<LibraryIngestionModeRecord?> GetAsync(string libraryId,
                                                            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        LibraryIngestionModeRecord? result = await mContext.LibraryIngestionModes
                                                          .Find(record => record.Id == libraryId)
                                                          .FirstOrDefaultAsync(ct);
        return result;
    }

    public async Task<bool> TryRenewAsync(string libraryId,
                                          LibraryIngestionMode mode,
                                          string ownerToken,
                                          DateTime updatedAtUtc,
                                          DateTime expiresAtUtc,
                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        ValidateLeasePeriod(updatedAtUtc, expiresAtUtc);
        var filter = OwnedFilter(libraryId, mode, ownerToken);
        var update = Builders<LibraryIngestionModeRecord>.Update
                     .Set(record => record.LeaseExpiresAtUtc, expiresAtUtc)
                     .Set(record => record.UpdatedAtUtc, updatedAtUtc);
        UpdateResult renewal = await mContext.LibraryIngestionModes.UpdateOneAsync(filter,
                                                                                    update,
                                                                                    cancellationToken: ct);
        return renewal.MatchedCount == 1;
    }

    public async Task<bool> TryCommitAsync(string libraryId,
                                           LibraryIngestionMode mode,
                                           string ownerToken,
                                           DateTime committedAtUtc,
                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        var filter = OwnedFilter(libraryId, mode, ownerToken);
        var update = Builders<LibraryIngestionModeRecord>.Update
                     .Set(record => record.OwnershipState, LibraryIngestionOwnershipState.Committed)
                     .Set(record => record.CommittedAtUtc, committedAtUtc)
                     .Set(record => record.UpdatedAtUtc, committedAtUtc);
        UpdateResult commit = await mContext.LibraryIngestionModes.UpdateOneAsync(filter,
                                                                                   update,
                                                                                   cancellationToken: ct);
        return commit.MatchedCount == 1;
    }

    public async Task<bool> TryReconcileReservedModeAsync(string libraryId,
                                                          LibraryIngestionMode expectedMode,
                                                          LibraryIngestionMode detectedMode,
                                                          string ownerToken,
                                                          DateTime committedAtUtc,
                                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        var filter = Builders<LibraryIngestionModeRecord>.Filter.And(
            OwnedFilter(libraryId, expectedMode, ownerToken),
            Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.OwnershipState,
                                                            LibraryIngestionOwnershipState.Reserved));
        var update = Builders<LibraryIngestionModeRecord>.Update
                     .Set(record => record.Mode, detectedMode)
                     .Set(record => record.OwnershipState, LibraryIngestionOwnershipState.Committed)
                     .Set(record => record.CommittedAtUtc, committedAtUtc)
                     .Set(record => record.UpdatedAtUtc, committedAtUtc);
        UpdateResult reconciliation = await mContext.LibraryIngestionModes.UpdateOneAsync(filter,
                                                                                           update,
                                                                                           cancellationToken: ct);
        return reconciliation.MatchedCount == 1;
    }

    public async Task<bool> TryReleaseAsync(string libraryId,
                                            LibraryIngestionMode mode,
                                            string ownerToken,
                                            DateTime updatedAtUtc,
                                            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        var filter = OwnedFilter(libraryId, mode, ownerToken);
        var update = Builders<LibraryIngestionModeRecord>.Update
                     .Set(record => record.LeaseOwnerToken, null)
                     .Set(record => record.LeaseExpiresAtUtc, null)
                     .Set(record => record.UpdatedAtUtc, updatedAtUtc);
        UpdateResult release = await mContext.LibraryIngestionModes.UpdateOneAsync(filter,
                                                                                    update,
                                                                                    cancellationToken: ct);
        return release.MatchedCount == 1;
    }

    public async Task<bool> TryAbandonReservationAsync(string libraryId,
                                                       LibraryIngestionMode mode,
                                                       string ownerToken,
                                                       CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        var filter = Builders<LibraryIngestionModeRecord>.Filter.And(
            OwnedFilter(libraryId, mode, ownerToken),
            Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.OwnershipState,
                                                            LibraryIngestionOwnershipState.Reserved));
        DeleteResult deletion = await mContext.LibraryIngestionModes.DeleteOneAsync(filter, ct);
        return deletion.DeletedCount == 1;
    }

    public async Task<bool> TryDeleteOwnershipAsync(string libraryId,
                                                    LibraryIngestionMode mode,
                                                    string ownerToken,
                                                    CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        DeleteResult deletion = await mContext.LibraryIngestionModes.DeleteOneAsync(
                                    OwnedFilter(libraryId, mode, ownerToken),
                                    ct);
        return deletion.DeletedCount == 1;
    }

    public async Task<bool> TryMarkPendingRenameAsync(string libraryId,
                                                      LibraryIngestionMode mode,
                                                      string ownerToken,
                                                      string renameOperationId,
                                                      DateTime updatedAtUtc,
                                                      CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        ArgumentException.ThrowIfNullOrEmpty(renameOperationId);
        var filter = Builders<LibraryIngestionModeRecord>.Filter.And(
            OwnedFilter(libraryId, mode, ownerToken),
            Builders<LibraryIngestionModeRecord>.Filter.Or(
                Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.PendingRenameOperationId, null),
                Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.PendingRenameOperationId,
                                                                renameOperationId)));
        var update = Builders<LibraryIngestionModeRecord>.Update
                     .Set(record => record.PendingRenameOperationId, renameOperationId)
                     .Set(record => record.UpdatedAtUtc, updatedAtUtc);
        UpdateResult result = await mContext.LibraryIngestionModes.UpdateOneAsync(filter,
                                                                                   update,
                                                                                   cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> TryClearPendingRenameAsync(string libraryId,
                                                       LibraryIngestionMode mode,
                                                       string ownerToken,
                                                       string renameOperationId,
                                                       DateTime updatedAtUtc,
                                                       CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(ownerToken);
        ArgumentException.ThrowIfNullOrEmpty(renameOperationId);
        var filter = Builders<LibraryIngestionModeRecord>.Filter.And(
            OwnedFilter(libraryId, mode, ownerToken),
            Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.PendingRenameOperationId,
                                                            renameOperationId));
        var update = Builders<LibraryIngestionModeRecord>.Update
                     .Set(record => record.PendingRenameOperationId, null)
                     .Set(record => record.UpdatedAtUtc, updatedAtUtc);
        UpdateResult result = await mContext.LibraryIngestionModes.UpdateOneAsync(filter,
                                                                                   update,
                                                                                   cancellationToken: ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> HasAnyLibraryDataAsync(string libraryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        LibraryIngestionDataEvidence evidence = await GetLibraryDataEvidenceAsync(libraryId, ct);
        return evidence.HasAnyData;
    }

    public async Task<LibraryIngestionDataEvidence> GetLibraryDataEvidenceAsync(
        string libraryId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        bool hasLibrary = await ExistsAsync(mContext.Libraries, record => record.Id == libraryId, ct);
        bool hasDefinition = await ExistsAsync(mContext.DirectoryLibraries, record => record.Id == libraryId, ct);
        bool hasDirectoryDocuments =
            await ExistsAsync(mContext.SourceDocuments, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.DocumentRevisions, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.SubjectCatalogs, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.SubjectAssignments, record => record.LibraryId == libraryId, ct);
        bool hasChildContent =
            await ExistsAsync(mContext.LibraryVersions, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.Pages, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.Chunks, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.VersionDiffs, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.LibraryProfiles, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.LibraryIndexes, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.Bm25Shards, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.ExcludedSymbols, record => record.LibraryId == libraryId, ct);
        bool hasOperationalHistory =
            await ExistsAsync(mContext.ScrapeAuditLog, record => record.LibraryId == libraryId, ct) ||
            await ExistsAsync(mContext.Jobs, record => record.LibraryId == libraryId, ct);
        var result = new LibraryIngestionDataEvidence(hasLibrary,
                                                      hasDefinition,
                                                      hasDirectoryDocuments,
                                                      hasChildContent,
                                                      hasOperationalHistory);
        return result;
    }

    private static async Task<bool> ExistsAsync<T>(IMongoCollection<T> collection,
                                                   System.Linq.Expressions.Expression<Func<T, bool>> predicate,
                                                   CancellationToken ct)
    {
        long count = await collection.CountDocumentsAsync(predicate,
                                                           new CountOptions { Limit = 1 },
                                                           ct);
        return count != 0;
    }

    private static FilterDefinition<LibraryIngestionModeRecord> OwnedFilter(string libraryId,
                                                                            LibraryIngestionMode mode,
                                                                            string ownerToken) =>
        Builders<LibraryIngestionModeRecord>.Filter.And(
            Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.Id, libraryId),
            Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.Mode, mode),
            Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.LeaseOwnerToken, ownerToken));

    private static void ValidateLeasePeriod(DateTime nowUtc,
                                            DateTime expiresAtUtc)
    {
        if (expiresAtUtc <= nowUtc)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc),
                                                  "The lease expiration must be later than its update time.");
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

    private const int DuplicateKeyErrorCode = 11000;
}
