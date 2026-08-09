// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using System.Text;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Ingestion.Services;

/// <summary>
///     Coordinates durable ingestion-mode fencing, retry-safe Mongo identity
///     changes, vector maintenance, and exact rename recovery.
/// </summary>
public sealed class LibraryRenameService : ILibraryRenameService
{
    public LibraryRenameService(RepositoryFactory repositoryFactory,
                                IVectorSearchProvider vectorSearch,
                                ILibraryIngestionModeLeaseManager modeLeaseManager,
                                TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(vectorSearch);
        ArgumentNullException.ThrowIfNull(modeLeaseManager);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mRepositoryFactory = repositoryFactory;
        mVectorSearch = vectorSearch;
        mModeLeaseManager = modeLeaseManager;
        mTimeProvider = timeProvider;
    }

    private readonly ILibraryIngestionModeLeaseManager mModeLeaseManager;
    private readonly RepositoryFactory mRepositoryFactory;
    private readonly TimeProvider mTimeProvider;
    private readonly IVectorSearchProvider mVectorSearch;

    public Task<RenameLibraryResponse> RenameLibraryAsync(string? profile,
                                                           string oldLibraryId,
                                                           string newLibraryId,
                                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(newLibraryId);
        return RenameAsync(profile,
                           LibraryRenameOperationKind.Library,
                           oldLibraryId,
                           newLibraryId,
                           sourceVersion: null,
                           targetVersion: null,
                           ct);
    }

    public Task<RenameLibraryResponse> RenameVersionAsync(string? profile,
                                                           string libraryId,
                                                           string oldVersion,
                                                           string newVersion,
                                                           CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(oldVersion);
        ArgumentException.ThrowIfNullOrEmpty(newVersion);
        return RenameAsync(profile,
                           LibraryRenameOperationKind.Version,
                           libraryId,
                           libraryId,
                           oldVersion,
                           newVersion,
                           ct);
    }

    private async Task<RenameLibraryResponse> RenameAsync(string? profile,
                                                          LibraryRenameOperationKind kind,
                                                          string sourceLibraryId,
                                                          string targetLibraryId,
                                                          string? sourceVersion,
                                                          string? targetVersion,
                                                          CancellationToken ct)
    {
        ILibraryRenameOperationRepository operations =
            mRepositoryFactory.GetLibraryRenameOperationRepository(profile);
        ILibraryRenameDataRepository data = mRepositoryFactory.GetLibraryRenameDataRepository(profile);
        LibraryRenameOperationRecord? operation = await operations.GetAsync(sourceLibraryId, ct);
        RenameLibraryResponse result;
        if (operation == null)
        {
            result = await BeginRenameAsync(profile,
                                            operations,
                                            data,
                                            kind,
                                            sourceLibraryId,
                                            targetLibraryId,
                                            sourceVersion,
                                            targetVersion,
                                            ct);
        }
        else
        {
            EnsureRequestedOperation(operation,
                                     kind,
                                     targetLibraryId,
                                     sourceVersion,
                                     targetVersion);
            await using RenameLeaseSet leases = await AcquireRecoveryLeasesAsync(profile,
                                                                                  operation,
                                                                                  ct);
            result = await ContinueRenameAsync(profile, operations, data, operation, leases, ct);
        }

        return result;
    }

    private async Task<RenameLibraryResponse> BeginRenameAsync(
        string? profile,
        ILibraryRenameOperationRepository operations,
        ILibraryRenameDataRepository data,
        LibraryRenameOperationKind kind,
        string sourceLibraryId,
        string targetLibraryId,
        string? sourceVersion,
        string? targetVersion,
        CancellationToken ct)
    {
        ILibraryIngestionModeRepository modes =
            mRepositoryFactory.GetLibraryIngestionModeRepository(profile);
        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
        LibraryIngestionModeRecord? sourceOwnership = await modes.GetAsync(sourceLibraryId, ct);
        if (sourceOwnership?.PendingRenameOperationId != null)
            throw new InvalidOperationException(
                $"Library '{sourceLibraryId}' has rename marker " +
                $"'{sourceOwnership.PendingRenameOperationId}' without a matching recovery operation.");
        DirectoryLibraryDefinition? sourceDefinition =
            await sources.GetDirectoryDefinitionAsync(sourceLibraryId, ct);
        LibraryIngestionMode mode = sourceOwnership?.Mode ??
                                    (sourceDefinition == null
                                         ? LibraryIngestionMode.Web
                                         : LibraryIngestionMode.Directory);
        string operationId = CreateOperationId(kind,
                                               mode,
                                               sourceLibraryId,
                                               targetLibraryId,
                                               sourceVersion,
                                               targetVersion);
        DateTime nowUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        var operation = new LibraryRenameOperationRecord
                            {
                                Id = sourceLibraryId,
                                OperationId = operationId,
                                Kind = kind,
                                State = LibraryRenameOperationState.Applying,
                                Mode = mode,
                                SourceLibraryId = sourceLibraryId,
                                TargetLibraryId = targetLibraryId,
                                SourceVersion = sourceVersion,
                                TargetVersion = targetVersion,
                                SourceRegistrationRevision = sourceDefinition?.RegistrationRevision,
                                SourceRegistrationIncarnationId = sourceDefinition?.RegistrationIncarnationId,
                                SourceLastPublishedVersion = sourceDefinition?.LastPublishedVersion,
                                TargetRegistrationIncarnationId = mode == LibraryIngestionMode.Directory
                                                                      ? Guid.NewGuid().ToString("N")
                                                                      : null,
                                StartedAtUtc = nowUtc,
                                UpdatedAtUtc = nowUtc
                            };

        IReadOnlyDictionary<string, LibraryIngestionModeRecord?> before =
            await GetOwnershipBeforeAsync(modes, operation, ct);
        await using RenameLeaseSet leases = await AcquireNormalLeasesAsync(profile,
                                                                            operation,
                                                                            before,
                                                                            ct);
        bool targetCollision = kind == LibraryRenameOperationKind.Library &&
                               leases.Get(targetLibraryId).OwnershipStateAtAcquisition ==
                               LibraryIngestionOwnershipState.Committed;
        RenameLibraryOutcome preflight = targetCollision
                                             ? RenameLibraryOutcome.Collision
                                             : await PreflightRenameAsync(data,
                                                                          kind,
                                                                          sourceLibraryId,
                                                                          targetLibraryId,
                                                                          sourceVersion,
                                                                          targetVersion,
                                                                          ct);

        RenameLibraryResponse result;
        if (preflight != RenameLibraryOutcome.Renamed)
        {
            await AbandonNewReservationsAsync(leases, before, ct);
            result = new RenameLibraryResponse(preflight, Counts: null);
        }
        else
        {
            if (mode == LibraryIngestionMode.Directory)
            {
                sourceDefinition = await sources.GetDirectoryDefinitionAsync(sourceLibraryId, ct) ??
                                   throw new InvalidOperationException(
                                       $"Directory definition '{sourceLibraryId}' disappeared before rename began.");
                if (sourceDefinition.PendingRenameOperationId != null ||
                    sourceDefinition.PublicationLeaseScanRunId != null)
                {
                    await AbandonNewReservationsAsync(leases, before, CancellationToken.None);
                    throw new InvalidOperationException(
                        $"Directory definition '{sourceLibraryId}' is busy with another lifecycle operation.");
                }

                if (operation.TargetRegistrationIncarnationId is not string targetIncarnation)
                    throw new InvalidOperationException("The directory rename target has no incarnation identity.");
                DirectoryLibraryDefinition targetSnapshot = kind == LibraryRenameOperationKind.Library
                    ? sourceDefinition with
                        {
                            Id = targetLibraryId,
                            RegistrationRevision = checked(sourceDefinition.RegistrationRevision + 1),
                            RegistrationIncarnationId = targetIncarnation,
                            PublicationLeaseScanRunId = null,
                            PublicationLeaseRegistrationRevision = null,
                            PublicationLeaseExpiresAtUtc = null,
                            PendingRenameOperationId = operationId
                        }
                    : sourceDefinition with
                        {
                            LastPublishedVersion = sourceDefinition.LastPublishedVersion == sourceVersion
                                                       ? targetVersion
                                                       : sourceDefinition.LastPublishedVersion,
                            RegistrationRevision = checked(sourceDefinition.RegistrationRevision + 1),
                            RegistrationIncarnationId = targetIncarnation,
                            PublicationLeaseScanRunId = null,
                            PublicationLeaseRegistrationRevision = null,
                            PublicationLeaseExpiresAtUtc = null,
                            PendingRenameOperationId = null
                        };
                operation = operation with
                                {
                                    SourceRegistrationRevision = sourceDefinition.RegistrationRevision,
                                    SourceRegistrationIncarnationId = sourceDefinition.RegistrationIncarnationId,
                                    SourceLastPublishedVersion = sourceDefinition.LastPublishedVersion,
                                    SourceDirectorySnapshot = sourceDefinition,
                                    TargetDirectorySnapshot = targetSnapshot
                                };
            }

            ILibraryIngestionModeLease sourceLease = leases.Get(sourceLibraryId);
            if (sourceLease.OwnershipStateAtAcquisition == LibraryIngestionOwnershipState.Reserved &&
                !await sourceLease.TryCommitAsync(ct))
                throw new InvalidOperationException("The source ingestion-mode lease was lost before rename began.");

            LibraryIngestionModeRecord sourceOwned = await modes.GetAsync(sourceLibraryId, ct) ??
                                                       throw new InvalidOperationException(
                                                           "The source ownership disappeared before rename began.");
            LibraryIngestionModeRecord targetOwned = kind == LibraryRenameOperationKind.Library
                                                         ? await modes.GetAsync(targetLibraryId, ct) ??
                                                           throw new InvalidOperationException(
                                                               "The target ownership disappeared before rename began.")
                                                         : sourceOwned;
            operation = operation with
                            {
                                SourceOwnershipReservedAtUtc = sourceOwned.ReservedAtUtc,
                                TargetOwnershipReservedAtUtc = targetOwned.ReservedAtUtc
                            };

            LibraryRenameOperationRecord? begun = await operations.TryBeginAsync(operation, ct);
            if (begun == null)
                throw new InvalidOperationException(
                    $"Library '{sourceLibraryId}' is already owned by a different rename operation.");
            operation = begun;
            await MarkPendingAsync(leases, operation.OperationId, ct);
            await data.PrepareDirectoryDefinitionsAsync(operation, ct);
            result = await ContinueRenameAsync(profile, operations, data, operation, leases, ct);
        }

        return result;
    }

    private static async Task<RenameLibraryOutcome> PreflightRenameAsync(
        ILibraryRenameDataRepository data,
        LibraryRenameOperationKind kind,
        string sourceLibraryId,
        string targetLibraryId,
        string? sourceVersion,
        string? targetVersion,
        CancellationToken ct)
    {
        RenameLibraryOutcome result = kind switch
                                          {
                                              LibraryRenameOperationKind.Library =>
                                                  await data.PreflightLibraryRenameAsync(sourceLibraryId,
                                                                                         targetLibraryId,
                                                                                         ct),
                                              LibraryRenameOperationKind.Version
                                                  when sourceVersion is string verifiedSourceVersion &&
                                                       targetVersion is string verifiedTargetVersion =>
                                                  await data.PreflightVersionRenameAsync(sourceLibraryId,
                                                                                         verifiedSourceVersion,
                                                                                         verifiedTargetVersion,
                                                                                         ct),
                                              LibraryRenameOperationKind.Version =>
                                                  throw new InvalidOperationException(
                                                      "A version rename requires exact source and target versions."),
                                              _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
                                          };
        return result;
    }

    private async Task<RenameLibraryResponse> ContinueRenameAsync(
        string? profile,
        ILibraryRenameOperationRepository operations,
        ILibraryRenameDataRepository data,
        LibraryRenameOperationRecord operation,
        RenameLeaseSet leases,
        CancellationToken ct)
    {
        RenameLibraryResult? counts = operation.Counts;
        string? maintenanceWarning = null;
        if (operation.State == LibraryRenameOperationState.Applying)
        {
            await MarkPendingAsync(leases, operation.OperationId, ct);
            await data.PrepareDirectoryDefinitionsAsync(operation, ct);
            using CancellationTokenSource owned = leases.CreateLinkedCancellation(ct);
            counts = operation.Kind == LibraryRenameOperationKind.Library
                         ? await data.ApplyLibraryRenameAsync(operation, owned.Token)
                         : await data.ApplyVersionRenameAsync(operation, owned.Token);
            owned.Token.ThrowIfCancellationRequested();
            operation = await AdvanceAsync(operations,
                                           operation,
                                           LibraryRenameOperationState.MongoCommitted,
                                           counts,
                                           ct);
        }

        if (operation.State == LibraryRenameOperationState.MongoCommitted)
        {
            if (operation.Kind == LibraryRenameOperationKind.Library &&
                !await leases.Get(operation.TargetLibraryId).TryCommitAsync(ct))
                throw new InvalidOperationException("The rename target ownership could not be committed.");
            try
            {
                using CancellationTokenSource owned = leases.CreateLinkedCancellation(ct);
                long rebuiltBm25Shards = await RebuildTargetAndRemoveSourceIndexesAsync(profile,
                                                                                         operation,
                                                                                         owned.Token);
                owned.Token.ThrowIfCancellationRequested();
                counts = (counts ?? throw new InvalidOperationException("The Mongo rename checkpoint has no result counts."))
                    with { Bm25Shards = rebuiltBm25Shards };
                operation = await AdvanceAsync(operations,
                                               operation,
                                               LibraryRenameOperationState.VectorCommitted,
                                               counts,
                                               ct);
            }
            catch(Exception exception) when(!ct.IsCancellationRequested && !leases.OwnershipLost)
            {
                maintenanceWarning = CreateMaintenanceWarning(operation, exception);
            }
        }

        RenameLibraryResponse result;
        if (maintenanceWarning != null)
            result = new RenameLibraryResponse(RenameLibraryOutcome.Renamed, counts, maintenanceWarning);
        else
        {
            if (operation.State != LibraryRenameOperationState.VectorCommitted)
                throw new InvalidOperationException("The rename operation has an unknown recovery checkpoint.");

            if (!await data.IsFinalizedAsync(operation, ct))
                await FinalizeRenameAsync(data, operation, leases, ct);

            await DeleteCompletedOperationAsync(operations, operation, ct);
            result = new RenameLibraryResponse(RenameLibraryOutcome.Renamed, counts);
        }

        return result;
    }

    private static string CreateMaintenanceWarning(LibraryRenameOperationRecord operation,
                                                   Exception exception)
    {
        string result = operation switch
                            {
                                { Kind: LibraryRenameOperationKind.Library } =>
                                    LibraryMaintenanceWarning(operation.TargetLibraryId, exception.Message),
                                { Kind: LibraryRenameOperationKind.Version, TargetVersion: string targetVersion } =>
                                    VersionMaintenanceWarning(operation.TargetLibraryId,
                                                              targetVersion,
                                                              exception.Message),
                                { Kind: LibraryRenameOperationKind.Version } =>
                                    throw new InvalidOperationException("The version rename target is missing.",
                                                                        exception),
                                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, null)
                            };
        return result;
    }

    private static async Task FinalizeRenameAsync(ILibraryRenameDataRepository data,
                                                  LibraryRenameOperationRecord operation,
                                                  RenameLeaseSet leases,
                                                  CancellationToken ct)
    {
        await MarkPendingAsync(leases, operation.OperationId, ct);
        await data.FinalizeDirectoryDefinitionsAsync(operation, ct);
        switch(operation.Kind)
        {
            case LibraryRenameOperationKind.Library:
                await FinalizeLibraryOwnershipAsync(operation, leases, ct);
                break;
            case LibraryRenameOperationKind.Version:
                await FinalizeVersionOwnershipAsync(operation, leases, ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, null);
        }

        if (!await data.IsFinalizedAsync(operation, ct))
            throw new InvalidOperationException("The rename did not reach an exact finalized state.");
    }

    private static async Task FinalizeLibraryOwnershipAsync(LibraryRenameOperationRecord operation,
                                                            RenameLeaseSet leases,
                                                            CancellationToken ct)
    {
        ILibraryIngestionModeLease target = leases.Get(operation.TargetLibraryId);
        if (!await target.TryCommitAsync(ct) ||
            !await target.TryClearPendingRenameAsync(operation.OperationId, ct))
            throw new InvalidOperationException("The target rename marker could not be cleared.");
        ILibraryIngestionModeLease source = leases.Get(operation.SourceLibraryId);
        if (!await source.TryDeleteOwnershipAsync(ct))
            throw new InvalidOperationException("The source rename ownership could not be deleted.");
    }

    private static async Task FinalizeVersionOwnershipAsync(LibraryRenameOperationRecord operation,
                                                            RenameLeaseSet leases,
                                                            CancellationToken ct)
    {
        ILibraryIngestionModeLease source = leases.Get(operation.SourceLibraryId);
        if (!await source.TryCommitAsync(ct) ||
            !await source.TryClearPendingRenameAsync(operation.OperationId, ct))
            throw new InvalidOperationException("The version rename marker could not be cleared.");
    }

    private async Task<LibraryRenameOperationRecord> AdvanceAsync(
        ILibraryRenameOperationRepository operations,
        LibraryRenameOperationRecord operation,
        LibraryRenameOperationState nextState,
        RenameLibraryResult? counts,
        CancellationToken ct)
    {
        DateTime updatedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        bool advanced = await operations.TryAdvanceAsync(operation.SourceLibraryId,
                                                          operation.OperationId,
                                                          operation.State,
                                                          nextState,
                                                          counts,
                                                          updatedAtUtc,
                                                          ct);
        LibraryRenameOperationRecord result;
        if (!advanced)
        {
            LibraryRenameOperationRecord? current = await operations.GetAsync(operation.SourceLibraryId, ct);
            if (current == null ||
                current.OperationId != operation.OperationId ||
                current.State != nextState)
                throw new InvalidOperationException("The durable rename checkpoint changed unexpectedly.");
            result = current;
        }
        else
            result = operation with { State = nextState, Counts = counts, UpdatedAtUtc = updatedAtUtc };

        return result;
    }

    private static async Task DeleteCompletedOperationAsync(
        ILibraryRenameOperationRepository operations,
        LibraryRenameOperationRecord operation,
        CancellationToken ct)
    {
        bool deleted = await operations.TryDeleteAsync(operation.SourceLibraryId,
                                                        operation.OperationId,
                                                        LibraryRenameOperationState.VectorCommitted,
                                                        ct);
        if (!deleted)
        {
            LibraryRenameOperationRecord? current = await operations.GetAsync(operation.SourceLibraryId, ct);
            if (current != null)
                throw new InvalidOperationException("The completed rename checkpoint could not be removed.");
        }
    }

    private async Task<long> RebuildTargetAndRemoveSourceIndexesAsync(string? profile,
                                                                      LibraryRenameOperationRecord operation,
                                                                      CancellationToken ct)
    {
        long result;
        if (operation.Kind == LibraryRenameOperationKind.Library)
        {
            ILibraryRepository libraries = mRepositoryFactory.GetLibraryRepository(profile);
            IReadOnlyList<LibraryVersionRecord> versions =
                await libraries.GetVersionsAsync(operation.TargetLibraryId, ct);
            long rebuiltBm25Shards = 0;
            foreach(LibraryVersionRecord version in versions.OrderBy(item => item.Version,
                                                                       StringComparer.Ordinal))
                rebuiltBm25Shards += await RebuildVersionIndexAsync(profile,
                                                                    operation.TargetLibraryId,
                                                                    version.Version,
                                                                    ct);
            await mVectorSearch.RemoveLibraryIndexesAsync(profile, operation.SourceLibraryId, ct);
            result = rebuiltBm25Shards;
        }
        else
        {
            if (operation.TargetVersion is not string targetVersion ||
                operation.SourceVersion is not string sourceVersion)
                throw new InvalidOperationException("The version rename index identity is incomplete.");
            long rebuiltVersionBm25Shards = await RebuildVersionIndexAsync(profile,
                                                                            operation.TargetLibraryId,
                                                                            targetVersion,
                                                                            ct);
            await mVectorSearch.RemoveIndexAsync(profile,
                                                 operation.SourceLibraryId,
                                                 sourceVersion,
                                                 ct);
            result = rebuiltVersionBm25Shards;
        }

        return result;
    }

    private async Task<long> RebuildVersionIndexAsync(string? profile,
                                                      string libraryId,
                                                      string version,
                                                      CancellationToken ct)
    {
        IChunkRepository chunks = mRepositoryFactory.GetChunkRepository(profile);
        IReadOnlyList<DocChunk> stored = await chunks.GetChunksAsync(libraryId, version, ct);
        Bm25BuildResult bm25 = Bm25IndexBuilder.Build(libraryId, version, stored);
        IBm25ShardRepository shards = mRepositoryFactory.GetBm25ShardRepository(profile);
        await shards.ReplaceShardsAsync(libraryId, version, bm25.Shards, ct);
        ILibraryIndexRepository indexes = mRepositoryFactory.GetLibraryIndexRepository(profile);
        LibraryIndex? existing = await indexes.GetAsync(libraryId, version, ct);
        LibraryIndex rebuilt = existing == null
                                   ? new LibraryIndex
                                         {
                                             Id = $"{libraryId}/{version}",
                                             LibraryId = libraryId,
                                             Version = version,
                                             Bm25 = bm25.Stats
                                         }
                                   : existing with { Bm25 = bm25.Stats };
        await indexes.UpsertAsync(rebuilt, ct);
        IReadOnlyList<DocChunk> embedded = stored.Where(chunk => chunk.Embedding != null).ToList();
        await mVectorSearch.IndexChunksAsync(profile, libraryId, version, embedded, ct);
        return bm25.Shards.Count;
    }

    private async Task<RenameLeaseSet> AcquireNormalLeasesAsync(string? profile,
                                                                LibraryRenameOperationRecord operation,
                                                                IReadOnlyDictionary<string,
                                                                    LibraryIngestionModeRecord?> before,
                                                                CancellationToken ct)
    {
        var result = new RenameLeaseSet();
        try
        {
            foreach(string libraryId in OperationLibraryIds(operation))
            {
                ILibraryIngestionModeLease? lease = await mModeLeaseManager.TryAcquireAsync(profile,
                                                                                             libraryId,
                                                                                             operation.Mode,
                                                                                             ct);
                if (lease == null)
                    throw new InvalidOperationException(
                        $"Library '{libraryId}' is busy or belongs to a different ingestion mode.");
                result.Add(lease);
            }

        }
        catch
        {
            try
            {
                await AbandonNewReservationsAsync(result, before, CancellationToken.None);
            }
            finally
            {
                await result.DisposeAsync();
            }

            throw;
        }

        return result;
    }

    private async Task<RenameLeaseSet> AcquireRecoveryLeasesAsync(string? profile,
                                                                  LibraryRenameOperationRecord operation,
                                                                  CancellationToken ct)
    {
        ILibraryIngestionModeRepository modes =
            mRepositoryFactory.GetLibraryIngestionModeRepository(profile);
        var records = new Dictionary<string, LibraryIngestionModeRecord?>(StringComparer.Ordinal);
        foreach(string libraryId in OperationLibraryIds(operation))
            records[libraryId] = await modes.GetAsync(libraryId, ct);

        LibraryIngestionModeRecord? sourceRecord = records[operation.SourceLibraryId];
        LibraryIngestionModeRecord? targetRecord = records[operation.TargetLibraryId];
        bool sourceGenerationChanged = sourceRecord != null &&
                                       sourceRecord.ReservedAtUtc != operation.SourceOwnershipReservedAtUtc;
        bool targetGenerationChanged = targetRecord != null &&
                                       targetRecord.ReservedAtUtc != operation.TargetOwnershipReservedAtUtc;
        if (targetGenerationChanged || sourceGenerationChanged)
            throw new InvalidOperationException("A rename ownership identity was replaced by a newer generation.");
        bool completedSource = operation.Kind == LibraryRenameOperationKind.Library &&
                               operation.State == LibraryRenameOperationState.VectorCommitted &&
                               sourceRecord == null;
        var result = new RenameLeaseSet();
        try
        {
            IEnumerable<string> recoveryLibraryIds = OperationLibraryIds(operation)
                .Where(libraryId => !completedSource || libraryId != operation.SourceLibraryId);
            foreach(string libraryId in recoveryLibraryIds)
                await AcquireRecoveryLeaseAsync(profile,
                                                operation,
                                                libraryId,
                                                records[libraryId],
                                                result,
                                                ct);

            if (completedSource)
            {
                ILibraryRenameDataRepository data =
                    mRepositoryFactory.GetLibraryRenameDataRepository(profile);
                if (!await data.IsFinalizedAsync(operation, ct))
                    throw new InvalidOperationException(
                        "The source ownership disappeared before the rename reached an exact finalized state.");
            }

        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }

        return result;
    }

    private async Task AcquireRecoveryLeaseAsync(string? profile,
                                                 LibraryRenameOperationRecord operation,
                                                 string libraryId,
                                                 LibraryIngestionModeRecord? record,
                                                 RenameLeaseSet leases,
                                                 CancellationToken ct)
    {
        if (record == null)
            throw new InvalidOperationException(
                $"Rename ownership for '{libraryId}' disappeared before completion.");
        bool recoverableMarker = record.PendingRenameOperationId == operation.OperationId ||
                                 record.PendingRenameOperationId == null &&
                                 operation.State is LibraryRenameOperationState.Applying or
                                     LibraryRenameOperationState.VectorCommitted;
        if (!recoverableMarker)
            throw new InvalidOperationException(
                $"Rename ownership for '{libraryId}' has a different pending operation.");
        ILibraryIngestionModeLease? lease =
            await mModeLeaseManager.TryAcquireRenameRecoveryAsync(profile,
                                                                   libraryId,
                                                                   operation.Mode,
                                                                   operation.OperationId,
                                                                   ct);
        if (lease == null)
            throw new InvalidOperationException($"Rename recovery could not acquire '{libraryId}'.");
        leases.Add(lease);
    }

    private static async Task MarkPendingAsync(RenameLeaseSet leases,
                                               string operationId,
                                               CancellationToken ct)
    {
        foreach(ILibraryIngestionModeLease lease in leases.Items)
        {
            if (!await lease.TryMarkPendingRenameAsync(operationId, ct))
                throw new InvalidOperationException(
                    $"Rename operation '{operationId}' lost ownership of '{lease.LibraryId}'.");
        }
    }

    private static async Task<IReadOnlyDictionary<string, LibraryIngestionModeRecord?>>
        GetOwnershipBeforeAsync(ILibraryIngestionModeRepository modes,
                                LibraryRenameOperationRecord operation,
                                CancellationToken ct)
    {
        var result = new Dictionary<string, LibraryIngestionModeRecord?>(StringComparer.Ordinal);
        foreach(string libraryId in OperationLibraryIds(operation))
            result[libraryId] = await modes.GetAsync(libraryId, ct);
        return result;
    }

    private static async Task AbandonNewReservationsAsync(
        RenameLeaseSet leases,
        IReadOnlyDictionary<string, LibraryIngestionModeRecord?> before,
        CancellationToken ct)
    {
        foreach(ILibraryIngestionModeLease lease in leases.Items)
        {
            if (before[lease.LibraryId] == null &&
                lease.OwnershipStateAtAcquisition == LibraryIngestionOwnershipState.Reserved &&
                !await lease.TryAbandonReservationAsync(ct))
                throw new InvalidOperationException(
                    $"The new ownership reservation for '{lease.LibraryId}' could not be abandoned.");
        }
    }

    private static IEnumerable<string> OperationLibraryIds(LibraryRenameOperationRecord operation)
    {
        IEnumerable<string> ids = operation.Kind == LibraryRenameOperationKind.Library
                                      ? [operation.SourceLibraryId, operation.TargetLibraryId]
                                      : [operation.SourceLibraryId];
        return ids.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal);
    }

    private static void EnsureRequestedOperation(LibraryRenameOperationRecord operation,
                                                 LibraryRenameOperationKind kind,
                                                 string targetLibraryId,
                                                 string? sourceVersion,
                                                 string? targetVersion)
    {
        bool exact = operation.Kind == kind &&
                     operation.TargetLibraryId.Equals(targetLibraryId, StringComparison.Ordinal) &&
                     string.Equals(operation.SourceVersion, sourceVersion, StringComparison.Ordinal) &&
                     string.Equals(operation.TargetVersion, targetVersion, StringComparison.Ordinal);
        if (!exact)
            throw new InvalidOperationException(
                $"Library '{operation.SourceLibraryId}' already has a different rename in progress.");
    }

    private static string CreateOperationId(LibraryRenameOperationKind kind,
                                            LibraryIngestionMode mode,
                                            string sourceLibraryId,
                                            string targetLibraryId,
                                            string? sourceVersion,
                                            string? targetVersion)
    {
        string identity = string.Join(UnitSeparator,
                                      kind,
                                      mode,
                                      sourceLibraryId,
                                      targetLibraryId,
                                      sourceVersion ?? string.Empty,
                                      targetVersion ?? string.Empty);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(digest);
    }

    private static string LibraryMaintenanceWarning(string libraryId, string detail) =>
        $"The MongoDB rename completed, but vector index maintenance failed: {detail} " +
        $"Retry rename_library for library '{libraryId}' to resume exact recovery.";

    private static string VersionMaintenanceWarning(string libraryId,
                                                    string version,
                                                    string detail) =>
        $"The MongoDB rename completed, but vector index maintenance failed: {detail} " +
        $"Retry rename_library for library '{libraryId}' version '{version}' to resume exact recovery.";

    private const char UnitSeparator = '\u001f';

    private sealed class RenameLeaseSet : IAsyncDisposable
    {
        private readonly Dictionary<string, ILibraryIngestionModeLease> mLeases =
            new(StringComparer.Ordinal);

        internal IEnumerable<ILibraryIngestionModeLease> Items =>
            mLeases.Values.OrderBy(lease => lease.LibraryId, StringComparer.Ordinal);

        internal bool OwnershipLost => mLeases.Values.Any(lease => lease.OwnershipLostToken.IsCancellationRequested);

        internal void Add(ILibraryIngestionModeLease lease)
        {
            ArgumentNullException.ThrowIfNull(lease);
            mLeases.Add(lease.LibraryId, lease);
        }

        internal ILibraryIngestionModeLease Get(string libraryId) =>
            mLeases.TryGetValue(libraryId, out ILibraryIngestionModeLease? lease)
                ? lease
                : throw new InvalidOperationException($"Rename lease '{libraryId}' is not held.");

        internal CancellationTokenSource CreateLinkedCancellation(CancellationToken ct)
        {
            CancellationToken[] tokens = [ct, .. mLeases.Values.Select(lease => lease.OwnershipLostToken)];
            return CancellationTokenSource.CreateLinkedTokenSource(tokens);
        }

        public async ValueTask DisposeAsync()
        {
            foreach(ILibraryIngestionModeLease lease in mLeases.Values
                                                                     .OrderByDescending(item => item.LibraryId,
                                                                                         StringComparer.Ordinal))
                await lease.DisposeAsync();
            mLeases.Clear();
        }
    }
}
