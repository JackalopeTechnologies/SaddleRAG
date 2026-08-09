// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Database.Repositories;

/// <summary>MongoDB implementation of durable rename recovery checkpoints.</summary>
public sealed class LibraryRenameOperationRepository : ILibraryRenameOperationRepository
{
    public LibraryRenameOperationRepository(SaddleRagDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        mOperations = context.LibraryRenameOperations;
    }

    private readonly IMongoCollection<LibraryRenameOperationRecord> mOperations;

    public async Task<LibraryRenameOperationRecord?> GetAsync(string sourceLibraryId,
                                                               CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceLibraryId);
        LibraryRenameOperationRecord? result = await mOperations.Find(operation =>
                                                               operation.Id == sourceLibraryId)
                                                              .FirstOrDefaultAsync(ct);
        return result;
    }

    public async Task<LibraryRenameOperationRecord?> TryBeginAsync(
        LibraryRenameOperationRecord operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateOperation(operation);
        LibraryRenameOperationRecord? result;
        try
        {
            await mOperations.InsertOneAsync(operation, cancellationToken: ct);
            result = operation;
        }
        catch(MongoWriteException exception) when(exception.WriteError?.Category ==
                                                   ServerErrorCategory.DuplicateKey)
        {
            LibraryRenameOperationRecord? existing = await GetAsync(operation.SourceLibraryId, ct);
            result = existing?.OperationId.Equals(operation.OperationId, StringComparison.Ordinal) == true
                         ? existing
                         : null;
        }

        return result;
    }

    private static void ValidateOperation(LibraryRenameOperationRecord operation)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation.Id);
        ArgumentException.ThrowIfNullOrEmpty(operation.OperationId);
        ArgumentException.ThrowIfNullOrEmpty(operation.SourceLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(operation.TargetLibraryId);
        if (!operation.Id.Equals(operation.SourceLibraryId, StringComparison.Ordinal))
            throw new ArgumentException("The operation identity must equal its source library identity.",
                                        nameof(operation));
        if (operation.State != LibraryRenameOperationState.Applying || operation.Counts != null)
            throw new ArgumentException("A new rename operation must begin at the Applying checkpoint.",
                                        nameof(operation));
        if (operation.SourceOwnershipReservedAtUtc == null || operation.TargetOwnershipReservedAtUtc == null)
            throw new ArgumentException("A rename operation requires exact source and target ownership identities.",
                                        nameof(operation));

        bool validShape = operation.Kind switch
                              {
                                  LibraryRenameOperationKind.Library =>
                                      operation.SourceVersion == null && operation.TargetVersion == null &&
                                      !operation.SourceLibraryId.Equals(operation.TargetLibraryId,
                                                                        StringComparison.Ordinal),
                                  LibraryRenameOperationKind.Version =>
                                      operation.SourceLibraryId.Equals(operation.TargetLibraryId,
                                                                        StringComparison.Ordinal) &&
                                      !string.IsNullOrEmpty(operation.SourceVersion) &&
                                      !string.IsNullOrEmpty(operation.TargetVersion) &&
                                      !string.Equals(operation.SourceVersion,
                                                     operation.TargetVersion,
                                                     StringComparison.Ordinal) &&
                                      operation.SourceOwnershipReservedAtUtc ==
                                      operation.TargetOwnershipReservedAtUtc,
                                  _ => false
                              };
        if (!validShape)
            throw new ArgumentException("The rename operation kind and source/target identities do not agree.",
                                        nameof(operation));
        bool directorySnapshotsValid = operation.Mode == LibraryIngestionMode.Directory
                                           ? operation.SourceDirectorySnapshot != null &&
                                             operation.TargetDirectorySnapshot != null
                                           : operation.SourceDirectorySnapshot == null &&
                                             operation.TargetDirectorySnapshot == null;
        if (!directorySnapshotsValid)
            throw new ArgumentException("The rename operation has invalid directory snapshots.",
                                        nameof(operation));
    }

    public async Task<bool> TryAdvanceAsync(string sourceLibraryId,
                                            string operationId,
                                            LibraryRenameOperationState expectedState,
                                            LibraryRenameOperationState nextState,
                                            RenameLibraryResult? counts,
                                            DateTime updatedAtUtc,
                                            CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        bool legalTransition = expectedState == LibraryRenameOperationState.Applying &&
                               nextState == LibraryRenameOperationState.MongoCommitted ||
                               expectedState == LibraryRenameOperationState.MongoCommitted &&
                               nextState == LibraryRenameOperationState.VectorCommitted;
        if (!legalTransition)
            throw new ArgumentException("The rename checkpoint transition is not legal.", nameof(nextState));
        FilterDefinition<LibraryRenameOperationRecord> filter =
            Builders<LibraryRenameOperationRecord>.Filter.And(
                Builders<LibraryRenameOperationRecord>.Filter.Eq(operation => operation.Id,
                                                                   sourceLibraryId),
                Builders<LibraryRenameOperationRecord>.Filter.Eq(operation => operation.OperationId,
                                                                   operationId),
                Builders<LibraryRenameOperationRecord>.Filter.Eq(operation => operation.State,
                                                                   expectedState));
        UpdateDefinition<LibraryRenameOperationRecord> update =
            Builders<LibraryRenameOperationRecord>.Update.Set(operation => operation.State, nextState)
                                                          .Set(operation => operation.UpdatedAtUtc, updatedAtUtc);
        if (counts != null)
            update = update.Set(operation => operation.Counts, counts);
        UpdateResult result = await mOperations.UpdateOneAsync(filter, update, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<bool> TryDeleteAsync(string sourceLibraryId,
                                           string operationId,
                                           LibraryRenameOperationState expectedState,
                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        if (expectedState != LibraryRenameOperationState.VectorCommitted)
            throw new ArgumentException("Only a vector-committed rename operation can be removed.",
                                        nameof(expectedState));
        FilterDefinition<LibraryRenameOperationRecord> filter =
            Builders<LibraryRenameOperationRecord>.Filter.And(
                Builders<LibraryRenameOperationRecord>.Filter.Eq(operation => operation.Id,
                                                                   sourceLibraryId),
                Builders<LibraryRenameOperationRecord>.Filter.Eq(operation => operation.OperationId,
                                                                   operationId),
                Builders<LibraryRenameOperationRecord>.Filter.Eq(operation => operation.State,
                                                                   expectedState));
        DeleteResult result = await mOperations.DeleteOneAsync(filter, ct);
        return result.DeletedCount == 1;
    }

}
