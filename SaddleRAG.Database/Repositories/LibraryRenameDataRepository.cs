// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using MongoDB.Driver;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     Fenced, retry-safe MongoDB mutation phases for durable library renames.
///     Every target row is upserted before any source row is removed.
/// </summary>
public sealed class LibraryRenameDataRepository : ILibraryRenameDataRepository
{
    public LibraryRenameDataRepository(SaddleRagDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        mContext = context;
        mOperations = context.LibraryRenameOperations;
    }

    private readonly SaddleRagDbContext mContext;
    private readonly IMongoCollection<LibraryRenameOperationRecord> mOperations;

    public async Task<RenameLibraryOutcome> PreflightLibraryRenameAsync(
        string sourceLibraryId,
        string targetLibraryId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(targetLibraryId);
        RenameLibraryOutcome result;
        if (sourceLibraryId.Equals(targetLibraryId, StringComparison.Ordinal))
            result = RenameLibraryOutcome.Collision;
        else
        {
            bool sourceExists = await ExistsAsync(mContext.Libraries,
                                                  Builders<LibraryRecord>.Filter.Eq(library => library.Id,
                                                                                     sourceLibraryId),
                                                  ct);
            if (!sourceExists)
                result = RenameLibraryOutcome.NotFound;
            else
                result = await HasLibraryIdentityAsync(targetLibraryId, ct) ||
                         await HasMappedLibraryTargetIdCollisionAsync(sourceLibraryId,
                                                                      targetLibraryId,
                                                                      ct) ||
                         await HasMappedArtifactClaimCollisionAsync(sourceLibraryId,
                                                                    targetLibraryId,
                                                                    sourceVersion: null,
                                                                    targetVersion: null,
                                                                    ct)
                             ? RenameLibraryOutcome.Collision
                             : RenameLibraryOutcome.Renamed;
        }
        return result;
    }

    public async Task<RenameLibraryOutcome> PreflightVersionRenameAsync(
        string libraryId,
        string sourceVersion,
        string targetVersion,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(sourceVersion);
        ArgumentException.ThrowIfNullOrEmpty(targetVersion);
        RenameLibraryOutcome result;
        if (sourceVersion.Equals(targetVersion, StringComparison.Ordinal))
            result = RenameLibraryOutcome.Collision;
        else
        {
            bool sourceExists = await ExistsAsync(mContext.LibraryVersions,
                                                  ByLibraryVersion<LibraryVersionRecord>(libraryId, sourceVersion),
                                                  ct);
            if (!sourceExists)
                result = RenameLibraryOutcome.NotFound;
            else
                result = await HasVersionIdentityAsync(libraryId, targetVersion, ct) ||
                         await HasMappedVersionTargetIdCollisionAsync(libraryId,
                                                                      sourceVersion,
                                                                      targetVersion,
                                                                      ct) ||
                         await HasMappedArtifactClaimCollisionAsync(libraryId,
                                                                    libraryId,
                                                                    sourceVersion,
                                                                    targetVersion,
                                                                    ct)
                             ? RenameLibraryOutcome.Collision
                             : RenameLibraryOutcome.Renamed;
        }
        return result;
    }

    public async Task PrepareDirectoryDefinitionsAsync(LibraryRenameOperationRecord operation,
                                                       CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateOperation(operation);
        await EnsureModeMarkersAsync(operation, ct);
        if (operation.Mode == LibraryIngestionMode.Web)
        {
            bool hasDirectoryDefinition = await ExistsAsync(
                                              mContext.DirectoryLibraries,
                                              Builders<DirectoryLibraryDefinition>.Filter.In(
                                                  definition => definition.Id,
                                                  operation.Kind == LibraryRenameOperationKind.Library
                                                      ? [operation.SourceLibraryId, operation.TargetLibraryId]
                                                      : [operation.SourceLibraryId]),
                                              ct);
            if (hasDirectoryDefinition)
                throw new InvalidOperationException("A web rename cannot own a directory-library definition.");
        }
        else
        {
            _ = operation.TargetRegistrationIncarnationId ??
                throw new InvalidOperationException(
                    "A directory rename requires a fresh target registration incarnation.");
            DirectoryLibraryDefinition sourceSnapshot = operation.SourceDirectorySnapshot ??
                                                        throw new InvalidOperationException(
                                                            "A directory rename requires its exact source snapshot.");
            DirectoryLibraryDefinition source = await mContext.DirectoryLibraries
                                                              .Find(definition =>
                                                                  definition.Id == operation.SourceLibraryId)
                                                              .FirstOrDefaultAsync(ct) ??
                                                throw new InvalidOperationException(
                                                    $"Directory definition '{operation.SourceLibraryId}' is missing.");
            EnsureExactDirectoryDefinition(source,
                                           source.PendingRenameOperationId == operation.OperationId
                                               ? sourceSnapshot with
                                                   {
                                                       PendingRenameOperationId = operation.OperationId
                                                   }
                                               : sourceSnapshot,
                                           SourceRole);
            await MarkSourceDirectoryPendingAsync(operation, source, ct);

            if (operation.Kind == LibraryRenameOperationKind.Library)
            {
                DirectoryLibraryDefinition target = operation.TargetDirectorySnapshot ??
                                                      throw new InvalidOperationException(
                                                          "A library rename requires its exact target snapshot.");
                await InsertExactTargetDirectoryDefinitionAsync(operation, target, ct);
            }
        }
    }

    private async Task InsertExactTargetDirectoryDefinitionAsync(LibraryRenameOperationRecord operation,
                                                                  DirectoryLibraryDefinition target,
                                                                  CancellationToken ct)
    {
        try
        {
            await mContext.DirectoryLibraries.InsertOneAsync(target, cancellationToken: ct);
        }
        catch(MongoWriteException exception) when(exception.WriteError?.Category ==
                                                   ServerErrorCategory.DuplicateKey)
        {
            DirectoryLibraryDefinition? existing = await mContext.DirectoryLibraries
                                                                  .Find(definition =>
                                                                      definition.Id ==
                                                                      operation.TargetLibraryId)
                                                                  .FirstOrDefaultAsync(ct);
            if (!DirectoryDefinitionsEqual(existing, target))
                throw new InvalidOperationException(
                    $"Rename target '{operation.TargetLibraryId}' contains a different directory definition.");
        }
    }

    public async Task<RenameLibraryResult> ApplyLibraryRenameAsync(
        LibraryRenameOperationRecord operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateOperation(operation, LibraryRenameOperationKind.Library);
        await EnsureModeMarkersAsync(operation, ct);
        string sourceId = operation.SourceLibraryId;
        string targetId = operation.TargetLibraryId;

        LibraryRecord? sourceLibrary = await mContext.Libraries.Find(library => library.Id == sourceId)
                                                             .FirstOrDefaultAsync(ct);
        if (sourceLibrary != null)
        {
            LibraryRecord target = LibraryRenameMapper.MapLibrary(sourceLibrary, targetId);
            await mContext.Libraries.ReplaceOneAsync(library => library.Id == targetId,
                                                      target,
                                                      smUpsertOptions,
                                                      ct);
        }

        await UpsertMappedAsync(mContext.LibraryVersions,
                                Builders<LibraryVersionRecord>.Filter.Eq(version => version.LibraryId, sourceId),
                                version => LibraryRenameMapper.MapLibraryVersion(version,
                                                                                 targetId,
                                                                                 version.Version),
                                version => version.Id,
                                ct);
        await UpsertMappedAsync(mContext.Chunks,
                                Builders<DocChunk>.Filter.Eq(chunk => chunk.LibraryId, sourceId),
                                chunk => LibraryRenameMapper.MapChunk(chunk, targetId, chunk.Version),
                                chunk => chunk.Id,
                                ct);
        await UpsertMappedAsync(mContext.Pages,
                                Builders<PageRecord>.Filter.Eq(page => page.LibraryId, sourceId),
                                page => LibraryRenameMapper.MapPage(page, targetId, page.Version),
                                page => page.Id,
                                ct);
        await UpsertMappedAsync(mContext.DocumentRevisions,
                                Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.LibraryId,
                                                                            sourceId),
                                revision => LibraryRenameMapper.MapDocumentRevision(revision,
                                                                                     targetId,
                                                                                     revision.Version),
                                revision => revision.Id,
                                ct);
        await UpsertMappedAsync(mContext.SubjectCatalogs,
                                Builders<SubjectCatalogRecord>.Filter.Eq(catalog => catalog.LibraryId, sourceId),
                                catalog => LibraryRenameMapper.MapSubjectCatalog(catalog, targetId),
                                catalog => catalog.Id,
                                ct);
        await UpsertMappedAsync(mContext.SubjectAssignments,
                                Builders<SubjectAssignmentRecord>.Filter.Eq(assignment => assignment.LibraryId,
                                                                             sourceId),
                                assignment => LibraryRenameMapper.MapSubjectAssignment(assignment,
                                                                                        targetId,
                                                                                        assignment.Version),
                                assignment => assignment.Id,
                                ct);
        await UpsertMappedAsync(mContext.LibraryProfiles,
                                Builders<LibraryProfile>.Filter.Eq(profile => profile.LibraryId, sourceId),
                                profile => profile with
                                               {
                                                   Id = RemapIdSegment(profile.Id, segmentIndex: 0, targetId),
                                                   LibraryId = targetId
                                               },
                                profile => profile.Id,
                                ct);
        await UpsertMappedAsync(mContext.LibraryIndexes,
                                Builders<LibraryIndex>.Filter.Eq(index => index.LibraryId, sourceId),
                                index => index with
                                             {
                                                 Id = RemapIdSegment(index.Id, segmentIndex: 0, targetId),
                                                 LibraryId = targetId,
                                                 Bm25 = new Bm25Stats()
                                             },
                                index => index.Id,
                                ct);
        await UpsertMappedAsync(mContext.ExcludedSymbols,
                                Builders<ExcludedSymbol>.Filter.Eq(symbol => symbol.LibraryId, sourceId),
                                symbol => symbol with
                                              {
                                                  Id = RemapIdSegment(symbol.Id, segmentIndex: 0, targetId),
                                                  LibraryId = targetId
                                              },
                                symbol => symbol.Id,
                                ct);
        await UpsertMappedAsync(mContext.VersionDiffs,
                                Builders<VersionDiffRecord>.Filter.Eq(diff => diff.LibraryId, sourceId),
                                diff => LibraryRenameMapper.MapVersionDiff(diff, targetId),
                                diff => diff.Id,
                                ct);
        await ReplaceSourceDocumentsAsync(sourceId,
                                          document => LibraryRenameMapper.MapSourceDocument(document, targetId),
                                          ct);
        await ReplaceProjectProfilesAsync(sourceId, targetId, ct);
        await ReplaceAuditEntriesAsync(sourceId,
                                       version => version,
                                       targetId,
                                       ct);
        await mContext.Jobs.UpdateManyAsync(Builders<JobRecord>.Filter.Eq(job => job.LibraryId, sourceId),
                                            Builders<JobRecord>.Update.Set(job => job.LibraryId, targetId),
                                            cancellationToken: ct);

        FilterDefinition<DocumentRevisionRecord> sourceRevisions =
            Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.LibraryId, sourceId);
        await RetargetDocumentArtifactClaimsAsync(sourceRevisions,
                                                   revision => SourceDocumentRepository.MakeRevisionId(
                                                       targetId,
                                                       revision.Version,
                                                       revision.DocumentId),
                                                   ct);

        await DeleteWholeSourceRowsAsync(sourceId, ct);
        await EnsureWholeSourceRowsRemovedAsync(sourceId, ct);
        if (!await ExistsAsync(mContext.Libraries,
                               Builders<LibraryRecord>.Filter.Eq(library => library.Id, targetId),
                               ct))
            throw new InvalidOperationException($"Rename target library '{targetId}' is incomplete.");
        RenameLibraryResult result = await CountLibraryTargetAsync(targetId, ct);
        return result;
    }

    public async Task<RenameLibraryResult> ApplyVersionRenameAsync(
        LibraryRenameOperationRecord operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateOperation(operation, LibraryRenameOperationKind.Version);
        await EnsureModeMarkersAsync(operation, ct);
        string libraryId = operation.SourceLibraryId;
        string sourceVersion = operation.SourceVersion ??
                               throw new InvalidOperationException("A version rename requires a source version.");
        string targetVersion = operation.TargetVersion ??
                               throw new InvalidOperationException("A version rename requires a target version.");

        await UpsertMappedAsync(mContext.LibraryVersions,
                                ByLibraryVersion<LibraryVersionRecord>(libraryId, sourceVersion),
                                version => LibraryRenameMapper.MapLibraryVersion(version,
                                                                                 libraryId,
                                                                                 targetVersion,
                                                                                 sourceVersion),
                                version => version.Id,
                                ct);
        await UpsertMappedAsync(mContext.Chunks,
                                ByLibraryVersion<DocChunk>(libraryId, sourceVersion),
                                chunk => LibraryRenameMapper.MapChunk(chunk, libraryId, targetVersion),
                                chunk => chunk.Id,
                                ct);
        await UpsertMappedAsync(mContext.Pages,
                                ByLibraryVersion<PageRecord>(libraryId, sourceVersion),
                                page => LibraryRenameMapper.MapPage(page, libraryId, targetVersion),
                                page => page.Id,
                                ct);
        await UpsertMappedAsync(mContext.DocumentRevisions,
                                ByLibraryVersion<DocumentRevisionRecord>(libraryId, sourceVersion),
                                revision => LibraryRenameMapper.MapDocumentRevision(revision,
                                                                                     libraryId,
                                                                                     targetVersion),
                                revision => revision.Id,
                                ct);
        await UpsertMappedAsync(mContext.SubjectAssignments,
                                ByLibraryVersion<SubjectAssignmentRecord>(libraryId, sourceVersion),
                                assignment => LibraryRenameMapper.MapSubjectAssignment(assignment,
                                                                                        libraryId,
                                                                                        targetVersion),
                                assignment => assignment.Id,
                                ct);
        await UpsertMappedAsync(mContext.LibraryProfiles,
                                ByLibraryVersion<LibraryProfile>(libraryId, sourceVersion),
                                profile => profile with
                                               {
                                                   Id = RemapIdSegment(profile.Id, segmentIndex: 1, targetVersion),
                                                   Version = targetVersion
                                               },
                                profile => profile.Id,
                                ct);
        await UpsertMappedAsync(mContext.LibraryIndexes,
                                ByLibraryVersion<LibraryIndex>(libraryId, sourceVersion),
                                index => index with
                                             {
                                                 Id = RemapIdSegment(index.Id, segmentIndex: 1, targetVersion),
                                                 Version = targetVersion,
                                                 Bm25 = new Bm25Stats()
                                             },
                                index => index.Id,
                                ct);
        await UpsertMappedAsync(mContext.ExcludedSymbols,
                                ByLibraryVersion<ExcludedSymbol>(libraryId, sourceVersion),
                                symbol => symbol with
                                              {
                                                  Id = RemapIdSegment(symbol.Id,
                                                                      segmentIndex: 1,
                                                                      targetVersion),
                                                  Version = targetVersion
                                              },
                                symbol => symbol.Id,
                                ct);
        FilterDefinition<VersionDiffRecord> sourceDiffs =
            Builders<VersionDiffRecord>.Filter.Eq(diff => diff.LibraryId, libraryId) &
            (Builders<VersionDiffRecord>.Filter.Eq(diff => diff.FromVersion, sourceVersion) |
             Builders<VersionDiffRecord>.Filter.Eq(diff => diff.ToVersion, sourceVersion));
        await UpsertMappedAsync(mContext.VersionDiffs,
                                sourceDiffs,
                                diff => LibraryRenameMapper.MapVersionDiff(diff,
                                                                           libraryId,
                                                                           sourceVersion,
                                                                           targetVersion),
                                diff => diff.Id,
                                ct);
        await ReplaceSourceDocumentsAsync(libraryId,
                                          document => LibraryRenameMapper.MapSourceDocument(document,
                                                                                             libraryId,
                                                                                             sourceVersion,
                                                                                             targetVersion),
                                          ct);
        await ReplacePreviousVersionReferencesAsync(libraryId, sourceVersion, targetVersion, ct);
        await ReplaceAuditEntriesAsync(libraryId,
                                       version => version.Equals(sourceVersion, StringComparison.Ordinal)
                                                      ? targetVersion
                                                      : version,
                                       libraryId,
                                       ct);
        await mContext.Jobs.UpdateManyAsync(ByLibraryVersion<JobRecord>(libraryId, sourceVersion),
                                            Builders<JobRecord>.Update.Set(job => job.Version, targetVersion),
                                            cancellationToken: ct);

        LibraryRecord? library = await mContext.Libraries.Find(record => record.Id == libraryId)
                                                        .FirstOrDefaultAsync(ct);
        if (library == null)
            throw new InvalidOperationException($"Library '{libraryId}' disappeared during version rename.");
        LibraryRecord mappedLibrary = LibraryRenameMapper.MapLibraryVersion(library,
                                                                             sourceVersion,
                                                                             targetVersion);
        await mContext.Libraries.ReplaceOneAsync(record => record.Id == libraryId,
                                                  mappedLibrary,
                                                  cancellationToken: ct);

        FilterDefinition<DocumentRevisionRecord> sourceRevisions =
            ByLibraryVersion<DocumentRevisionRecord>(libraryId, sourceVersion);
        await RetargetDocumentArtifactClaimsAsync(sourceRevisions,
                                                   revision => SourceDocumentRepository.MakeRevisionId(
                                                       libraryId,
                                                       targetVersion,
                                                       revision.DocumentId),
                                                   ct);
        await DeleteVersionSourceRowsAsync(libraryId, sourceVersion, sourceDiffs, ct);
        await EnsureVersionSourceRowsRemovedAsync(libraryId, sourceVersion, ct);
        RenameLibraryResult result = await CountVersionTargetAsync(libraryId, targetVersion, ct);
        return result;
    }

    public async Task FinalizeDirectoryDefinitionsAsync(LibraryRenameOperationRecord operation,
                                                        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateOperationState(operation, LibraryRenameOperationState.VectorCommitted);
        await EnsureModeMarkersAsync(operation, ct);
        if (operation.Mode != LibraryIngestionMode.Web)
            await FinalizeDirectoryDefinitionsCoreAsync(operation, ct);
    }

    public async Task<bool> IsFinalizedAsync(LibraryRenameOperationRecord operation,
                                             CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateOperationState(operation, LibraryRenameOperationState.VectorCommitted);
        LibraryIngestionModeRecord? sourceMode = await mContext.LibraryIngestionModes
                                                              .Find(item =>
                                                                  item.Id == operation.SourceLibraryId)
                                                              .FirstOrDefaultAsync(ct);
        LibraryIngestionModeRecord? targetMode = operation.Kind == LibraryRenameOperationKind.Library
                                                     ? await mContext.LibraryIngestionModes
                                                                     .Find(item =>
                                                                         item.Id == operation.TargetLibraryId)
                                                                     .FirstOrDefaultAsync(ct)
                                                     : sourceMode;
        bool ownershipFinal = operation.Kind == LibraryRenameOperationKind.Library
                                  ? sourceMode == null && targetMode is
                                        {
                                            OwnershipState: LibraryIngestionOwnershipState.Committed,
                                            PendingRenameOperationId: null
                                        } && targetMode.Mode == operation.Mode &&
                                        targetMode.ReservedAtUtc == operation.TargetOwnershipReservedAtUtc
                                  : sourceMode is
                                        {
                                            OwnershipState: LibraryIngestionOwnershipState.Committed,
                                            PendingRenameOperationId: null
                                        } && sourceMode.Mode == operation.Mode &&
                                        sourceMode.ReservedAtUtc == operation.SourceOwnershipReservedAtUtc;
        bool result = false;
        if (ownershipFinal)
        {
            bool dataFinal;
            if (operation.Kind == LibraryRenameOperationKind.Library)
                dataFinal = !await HasLibraryIdentityExceptOperationAsync(operation.SourceLibraryId, ct) &&
                            await ExistsAsync(mContext.Libraries,
                                              Builders<LibraryRecord>.Filter.Eq(item => item.Id,
                                                                                operation.TargetLibraryId),
                                              ct);
            else
            {
                string sourceVersion = operation.SourceVersion ??
                                       throw new InvalidOperationException(
                                           "A version rename requires a source version.");
                string targetVersion = operation.TargetVersion ??
                                       throw new InvalidOperationException(
                                           "A version rename requires a target version.");
                dataFinal = !await HasVersionSourceRowsAsync(operation.SourceLibraryId, sourceVersion, ct) &&
                            await ExistsAsync(mContext.LibraryVersions,
                                              ByLibraryVersion<LibraryVersionRecord>(operation.TargetLibraryId,
                                                                                    targetVersion),
                                              ct);
            }

            if (dataFinal)
                result = operation.Mode == LibraryIngestionMode.Web ||
                         await IsDirectoryDefinitionFinalizedAsync(operation, ct);
        }
        return result;
    }

    private async Task FinalizeDirectoryDefinitionsCoreAsync(LibraryRenameOperationRecord operation,
                                                             CancellationToken ct)
    {
        _ = operation.TargetRegistrationIncarnationId ??
            throw new InvalidOperationException(
                "A directory rename requires a fresh target registration incarnation.");
        DirectoryLibraryDefinition sourceSnapshot = operation.SourceDirectorySnapshot ??
                                                    throw new InvalidOperationException(
                                                        "A directory rename requires its exact source snapshot.");
        DirectoryLibraryDefinition targetSnapshot = operation.TargetDirectorySnapshot ??
                                                    throw new InvalidOperationException(
                                                        "A directory rename requires its exact target snapshot.");
        if (operation.Kind == LibraryRenameOperationKind.Library)
        {
            DirectoryLibraryDefinition? target = await mContext.DirectoryLibraries
                                                               .Find(definition =>
                                                                   definition.Id == operation.TargetLibraryId)
                                                               .FirstOrDefaultAsync(ct);
            if (target?.PendingRenameOperationId == operation.OperationId &&
                DirectoryDefinitionsEqual(target, targetSnapshot))
            {
                DirectoryLibraryDefinition active = targetSnapshot with { PendingRenameOperationId = null };
                ReplaceOneResult activated = await mContext.DirectoryLibraries.ReplaceOneAsync(
                    definition => definition.Id == operation.TargetLibraryId &&
                                  definition.PendingRenameOperationId == operation.OperationId &&
                                  definition.RegistrationIncarnationId ==
                                  targetSnapshot.RegistrationIncarnationId,
                    active,
                    cancellationToken: ct);
                if (activated.ModifiedCount != 1)
                    throw new InvalidOperationException("The target directory definition changed during rename.");
            }
            else
            {
                if (!DirectoryDefinitionsEqual(target,
                                               targetSnapshot with
                                                   {
                                                       PendingRenameOperationId = null
                                                   }))
                    throw new InvalidOperationException(
                        "The target directory definition is not owned by this rename.");
            }

            DirectoryLibraryDefinition? source = await mContext.DirectoryLibraries
                                                               .Find(definition =>
                                                                   definition.Id == operation.SourceLibraryId)
                                                               .FirstOrDefaultAsync(ct);
            if (source != null)
            {
                EnsureExactDirectoryDefinition(source,
                                               sourceSnapshot with
                                                   {
                                                       PendingRenameOperationId = operation.OperationId
                                                   },
                                               SourceRole);
                DeleteResult deleted = await mContext.DirectoryLibraries.DeleteOneAsync(
                    definition => definition.Id == operation.SourceLibraryId &&
                                  definition.PendingRenameOperationId == operation.OperationId &&
                                  definition.RegistrationRevision == operation.SourceRegistrationRevision &&
                                  definition.RegistrationIncarnationId ==
                                  operation.SourceRegistrationIncarnationId,
                    ct);
                if (deleted.DeletedCount != 1)
                    throw new InvalidOperationException("The source directory definition changed during rename.");
            }
        }
        else
        {
            DirectoryLibraryDefinition? definition = await mContext.DirectoryLibraries
                                                                   .Find(item =>
                                                                       item.Id == operation.SourceLibraryId)
                                                                   .FirstOrDefaultAsync(ct);
            if (definition?.PendingRenameOperationId == operation.OperationId)
            {
                EnsureExactDirectoryDefinition(definition,
                                               sourceSnapshot with
                                                   {
                                                       PendingRenameOperationId = operation.OperationId
                                                   },
                                               SourceRole);
                DirectoryLibraryDefinition active = targetSnapshot;
                ReplaceOneResult completed = await mContext.DirectoryLibraries.ReplaceOneAsync(
                    item => item.Id == operation.SourceLibraryId &&
                            item.PendingRenameOperationId == operation.OperationId &&
                            item.RegistrationRevision == operation.SourceRegistrationRevision &&
                            item.RegistrationIncarnationId == operation.SourceRegistrationIncarnationId,
                    active,
                    cancellationToken: ct);
                if (completed.ModifiedCount != 1)
                    throw new InvalidOperationException("The directory definition changed during version rename.");
            }
            else
            {
                if (!DirectoryDefinitionsEqual(definition, targetSnapshot))
                    throw new InvalidOperationException(
                        "The directory definition is not finalized for this rename.");
            }
        }
    }

    private async Task<bool> HasLibraryIdentityAsync(string libraryId, CancellationToken ct)
    {
        bool result = await ExistsAsync(mContext.Libraries,
                                        Builders<LibraryRecord>.Filter.Eq(item => item.Id, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.LibraryVersions,
                                        Builders<LibraryVersionRecord>.Filter.Eq(item => item.LibraryId,
                                                                                  libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.Chunks,
                                        Builders<DocChunk>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.Pages,
                                        Builders<PageRecord>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.DirectoryLibraries,
                                        Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id,
                                                                                        libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.SourceDocuments,
                                        Builders<SourceDocumentRecord>.Filter.Eq(item => item.LibraryId,
                                                                                  libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.DocumentRevisions,
                                        Builders<DocumentRevisionRecord>.Filter.Eq(item => item.LibraryId,
                                                                                    libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.SubjectCatalogs,
                                        Builders<SubjectCatalogRecord>.Filter.Eq(item => item.LibraryId,
                                                                                  libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.SubjectAssignments,
                                        Builders<SubjectAssignmentRecord>.Filter.Eq(item => item.LibraryId,
                                                                                     libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.LibraryProfiles,
                                        Builders<LibraryProfile>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.LibraryIndexes,
                                        Builders<LibraryIndex>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.Bm25Shards,
                                        Builders<Bm25Shard>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.ExcludedSymbols,
                                        Builders<ExcludedSymbol>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.VersionDiffs,
                                        Builders<VersionDiffRecord>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.Jobs,
                                        Builders<JobRecord>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.ScrapeAuditLog,
                                        Builders<ScrapeAuditLogEntry>.Filter.Eq(item => item.LibraryId,
                                                                                libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.ProjectProfiles,
                                        Builders<ProjectProfile>.Filter.AnyEq(item => item.IngestedPackages,
                                                                               libraryId),
                                        ct) ||
                      await ExistsAsync(mOperations,
                                        Builders<LibraryRenameOperationRecord>.Filter.Eq(item => item.Id,
                                                                                          libraryId),
                                        ct);
        return result;
    }

    private async Task<bool> HasLibraryIdentityExceptOperationAsync(string libraryId,
                                                                    CancellationToken ct)
    {
        bool result = await HasNonOperationLibraryIdentityAsync(libraryId, ct);
        return result;
    }

    private async Task<bool> HasNonOperationLibraryIdentityAsync(string libraryId,
                                                                 CancellationToken ct)
    {
        bool result = await ExistsAsync(mContext.Libraries,
                                        Builders<LibraryRecord>.Filter.Eq(item => item.Id, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.LibraryVersions,
                                        Builders<LibraryVersionRecord>.Filter.Eq(item => item.LibraryId,
                                                                                  libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.Chunks,
                                        Builders<DocChunk>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.Pages,
                                        Builders<PageRecord>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.DirectoryLibraries,
                                        Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id,
                                                                                        libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.SourceDocuments,
                                        Builders<SourceDocumentRecord>.Filter.Eq(item => item.LibraryId,
                                                                                  libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.DocumentRevisions,
                                        Builders<DocumentRevisionRecord>.Filter.Eq(item => item.LibraryId,
                                                                                    libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.SubjectCatalogs,
                                        Builders<SubjectCatalogRecord>.Filter.Eq(item => item.LibraryId,
                                                                                  libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.SubjectAssignments,
                                        Builders<SubjectAssignmentRecord>.Filter.Eq(item => item.LibraryId,
                                                                                     libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.LibraryProfiles,
                                        Builders<LibraryProfile>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.LibraryIndexes,
                                        Builders<LibraryIndex>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.Bm25Shards,
                                        Builders<Bm25Shard>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.ExcludedSymbols,
                                        Builders<ExcludedSymbol>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.VersionDiffs,
                                        Builders<VersionDiffRecord>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.Jobs,
                                        Builders<JobRecord>.Filter.Eq(item => item.LibraryId, libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.ScrapeAuditLog,
                                        Builders<ScrapeAuditLogEntry>.Filter.Eq(item => item.LibraryId,
                                                                                libraryId),
                                        ct) ||
                      await ExistsAsync(mContext.ProjectProfiles,
                                        Builders<ProjectProfile>.Filter.AnyEq(item => item.IngestedPackages,
                                                                               libraryId),
                                        ct);
        return result;
    }

    private async Task<bool> IsDirectoryDefinitionFinalizedAsync(
        LibraryRenameOperationRecord operation,
        CancellationToken ct)
    {
        DirectoryLibraryDefinition? source = await mContext.DirectoryLibraries
                                                          .Find(item => item.Id == operation.SourceLibraryId)
                                                          .FirstOrDefaultAsync(ct);
        bool result;
        if (operation.Kind == LibraryRenameOperationKind.Library)
        {
            DirectoryLibraryDefinition? target = await mContext.DirectoryLibraries
                                                              .Find(item =>
                                                                  item.Id == operation.TargetLibraryId)
                                                              .FirstOrDefaultAsync(ct);
            DirectoryLibraryDefinition targetSnapshot = operation.TargetDirectorySnapshot ??
                                                        throw new InvalidOperationException(
                                                            "The rename target snapshot is missing.");
            DirectoryLibraryDefinition expected = targetSnapshot with
                                                       {
                                                           PendingRenameOperationId = null
                                                       };
            result = source == null && DirectoryDefinitionsEqual(target, expected);
        }
        else
            result = DirectoryDefinitionsEqual(
                source,
                operation.TargetDirectorySnapshot ??
                throw new InvalidOperationException("The version rename target snapshot is missing."));
        return result;
    }

    private async Task<bool> HasVersionIdentityAsync(string libraryId,
                                                     string version,
                                                     CancellationToken ct)
    {
        bool result = await HasVersionSourceRowsAsync(libraryId, version, ct) ||
                      await ExistsAsync(mContext.DirectoryLibraries,
                                        Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id,
                                                                                        libraryId) &
                                        Builders<DirectoryLibraryDefinition>.Filter.Eq(
                                            item => item.LastPublishedVersion,
                                            version),
                                        ct);
        return result;
    }

    private async Task<bool> HasVersionSourceRowsAsync(string libraryId,
                                                       string version,
                                                       CancellationToken ct)
    {
        FilterDefinition<LibraryRecord> libraryPointer =
            Builders<LibraryRecord>.Filter.Eq(item => item.Id, libraryId) &
            (Builders<LibraryRecord>.Filter.Eq(item => item.CurrentVersion, version) |
             Builders<LibraryRecord>.Filter.AnyEq(item => item.AllVersions, version));
        FilterDefinition<SourceDocumentRecord> sourcePointer =
            Builders<SourceDocumentRecord>.Filter.Eq(item => item.LibraryId, libraryId) &
            (Builders<SourceDocumentRecord>.Filter.Eq(item => item.FirstSeenVersion, version) |
             Builders<SourceDocumentRecord>.Filter.Eq(item => item.LastSeenVersion, version));
        FilterDefinition<VersionDiffRecord> diffPointer =
            Builders<VersionDiffRecord>.Filter.Eq(item => item.LibraryId, libraryId) &
            (Builders<VersionDiffRecord>.Filter.Eq(item => item.FromVersion, version) |
             Builders<VersionDiffRecord>.Filter.Eq(item => item.ToVersion, version));
        bool result = await ExistsAsync(mContext.Libraries, libraryPointer, ct) ||
                      await ExistsAsync(mContext.LibraryVersions,
                                        ByLibraryVersion<LibraryVersionRecord>(libraryId, version),
                                        ct) ||
                      await ExistsAsync(mContext.LibraryVersions,
                                        Builders<LibraryVersionRecord>.Filter.Eq(item => item.LibraryId,
                                                                                  libraryId) &
                                        Builders<LibraryVersionRecord>.Filter.Eq(item => item.PreviousVersion,
                                                                                  version),
                                        ct) ||
                      await ExistsAsync(mContext.Chunks, ByLibraryVersion<DocChunk>(libraryId, version), ct) ||
                      await ExistsAsync(mContext.Pages, ByLibraryVersion<PageRecord>(libraryId, version), ct) ||
                      await ExistsAsync(mContext.SourceDocuments, sourcePointer, ct) ||
                      await ExistsAsync(mContext.DocumentRevisions,
                                        ByLibraryVersion<DocumentRevisionRecord>(libraryId, version),
                                        ct) ||
                      await ExistsAsync(mContext.SubjectAssignments,
                                        ByLibraryVersion<SubjectAssignmentRecord>(libraryId, version),
                                        ct) ||
                      await ExistsAsync(mContext.LibraryProfiles,
                                        ByLibraryVersion<LibraryProfile>(libraryId, version),
                                        ct) ||
                      await ExistsAsync(mContext.LibraryIndexes,
                                        ByLibraryVersion<LibraryIndex>(libraryId, version),
                                        ct) ||
                      await ExistsAsync(mContext.Bm25Shards,
                                        ByLibraryVersion<Bm25Shard>(libraryId, version),
                                        ct) ||
                      await ExistsAsync(mContext.ExcludedSymbols,
                                        ByLibraryVersion<ExcludedSymbol>(libraryId, version),
                                        ct) ||
                      await ExistsAsync(mContext.VersionDiffs, diffPointer, ct) ||
                      await ExistsAsync(mContext.Jobs, ByLibraryVersion<JobRecord>(libraryId, version), ct) ||
                      await ExistsAsync(mContext.ScrapeAuditLog,
                                        ByLibraryVersion<ScrapeAuditLogEntry>(libraryId, version),
                                        ct);
        return result;
    }

    private async Task<bool> HasMappedArtifactClaimCollisionAsync(string sourceLibraryId,
                                                                  string targetLibraryId,
                                                                  string? sourceVersion,
                                                                  string? targetVersion,
                                                                  CancellationToken ct)
    {
        FilterDefinition<DocumentRevisionRecord> filter = sourceVersion == null
            ? Builders<DocumentRevisionRecord>.Filter.Eq(revision => revision.LibraryId, sourceLibraryId)
            : ByLibraryVersion<DocumentRevisionRecord>(sourceLibraryId, sourceVersion);
        IReadOnlyList<DocumentRevisionRecord> revisions = await mContext.DocumentRevisions.Find(filter)
                                                                             .ToListAsync(ct);
        bool result = false;
        foreach(DocumentRevisionRecord revision in revisions)
        {
            if (!result)
            {
                string mappedVersion = targetVersion ?? revision.Version;
                string mappedId = SourceDocumentRepository.MakeRevisionId(targetLibraryId,
                                                                            mappedVersion,
                                                                            revision.DocumentId);
                bool targetRevisionExists = await ExistsAsync(
                                                mContext.DocumentRevisions,
                                                Builders<DocumentRevisionRecord>.Filter.Eq(item => item.Id,
                                                                                            mappedId),
                                                ct);
                FilterDefinition<DocumentArtifactBlobRecord> targetClaim =
                    Builders<DocumentArtifactBlobRecord>.Filter.ElemMatch(
                        artifact => artifact.Claims,
                        Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.RevisionId, mappedId));
                result = targetRevisionExists ||
                         await ExistsAsync(mContext.DocumentArtifactBlobs, targetClaim, ct);
            }
        }
        return result;
    }

    private async Task<bool> HasMappedLibraryTargetIdCollisionAsync(string sourceLibraryId,
                                                                    string targetLibraryId,
                                                                    CancellationToken ct)
    {
        bool result = await HasMappedTargetIdCollisionAsync(
                          mContext.LibraryVersions,
                          Builders<LibraryVersionRecord>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => LibraryRenameMapper.MapLibraryVersion(item, targetLibraryId, item.Version),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.Chunks,
                          Builders<DocChunk>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => LibraryRenameMapper.MapChunk(item, targetLibraryId, item.Version),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.Pages,
                          Builders<PageRecord>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => LibraryRenameMapper.MapPage(item, targetLibraryId, item.Version),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.DocumentRevisions,
                          Builders<DocumentRevisionRecord>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => LibraryRenameMapper.MapDocumentRevision(item,
                                                                           targetLibraryId,
                                                                           item.Version),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.SubjectCatalogs,
                          Builders<SubjectCatalogRecord>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => LibraryRenameMapper.MapSubjectCatalog(item, targetLibraryId),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.SubjectAssignments,
                          Builders<SubjectAssignmentRecord>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => LibraryRenameMapper.MapSubjectAssignment(item,
                                                                            targetLibraryId,
                                                                            item.Version),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.LibraryProfiles,
                          Builders<LibraryProfile>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => item with
                                      {
                                          Id = RemapIdSegment(item.Id, segmentIndex: 0, targetLibraryId),
                                          LibraryId = targetLibraryId
                                      },
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.LibraryIndexes,
                          Builders<LibraryIndex>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => item with
                                      {
                                          Id = RemapIdSegment(item.Id, segmentIndex: 0, targetLibraryId),
                                          LibraryId = targetLibraryId
                                      },
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.Bm25Shards,
                          Builders<Bm25Shard>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => item with
                                      {
                                          Id = RemapIdSegment(item.Id, segmentIndex: 0, targetLibraryId),
                                          LibraryId = targetLibraryId
                                      },
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.ExcludedSymbols,
                          Builders<ExcludedSymbol>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => item with
                                      {
                                          Id = RemapIdSegment(item.Id, segmentIndex: 0, targetLibraryId),
                                          LibraryId = targetLibraryId
                                      },
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.VersionDiffs,
                          Builders<VersionDiffRecord>.Filter.Eq(item => item.LibraryId, sourceLibraryId),
                          item => LibraryRenameMapper.MapVersionDiff(item, targetLibraryId),
                          item => item.Id,
                          ct);
        return result;
    }

    private async Task<bool> HasMappedVersionTargetIdCollisionAsync(string libraryId,
                                                                    string sourceVersion,
                                                                    string targetVersion,
                                                                    CancellationToken ct)
    {
        FilterDefinition<VersionDiffRecord> sourceDiffs =
            Builders<VersionDiffRecord>.Filter.Eq(item => item.LibraryId, libraryId) &
            (Builders<VersionDiffRecord>.Filter.Eq(item => item.FromVersion, sourceVersion) |
             Builders<VersionDiffRecord>.Filter.Eq(item => item.ToVersion, sourceVersion));
        bool result = await HasMappedTargetIdCollisionAsync(
                          mContext.LibraryVersions,
                          ByLibraryVersion<LibraryVersionRecord>(libraryId, sourceVersion),
                          item => LibraryRenameMapper.MapLibraryVersion(item,
                                                                         libraryId,
                                                                         targetVersion,
                                                                         sourceVersion),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.Chunks,
                          ByLibraryVersion<DocChunk>(libraryId, sourceVersion),
                          item => LibraryRenameMapper.MapChunk(item, libraryId, targetVersion),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.Pages,
                          ByLibraryVersion<PageRecord>(libraryId, sourceVersion),
                          item => LibraryRenameMapper.MapPage(item, libraryId, targetVersion),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.DocumentRevisions,
                          ByLibraryVersion<DocumentRevisionRecord>(libraryId, sourceVersion),
                          item => LibraryRenameMapper.MapDocumentRevision(item, libraryId, targetVersion),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.SubjectAssignments,
                          ByLibraryVersion<SubjectAssignmentRecord>(libraryId, sourceVersion),
                          item => LibraryRenameMapper.MapSubjectAssignment(item, libraryId, targetVersion),
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.LibraryProfiles,
                          ByLibraryVersion<LibraryProfile>(libraryId, sourceVersion),
                          item => item with
                                      {
                                          Id = RemapIdSegment(item.Id, segmentIndex: 1, targetVersion),
                                          Version = targetVersion
                                      },
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.LibraryIndexes,
                          ByLibraryVersion<LibraryIndex>(libraryId, sourceVersion),
                          item => item with
                                      {
                                          Id = RemapIdSegment(item.Id, segmentIndex: 1, targetVersion),
                                          Version = targetVersion
                                      },
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.Bm25Shards,
                          ByLibraryVersion<Bm25Shard>(libraryId, sourceVersion),
                          item => item with
                                      {
                                          Id = RemapIdSegment(item.Id, segmentIndex: 1, targetVersion),
                                          Version = targetVersion
                                      },
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.ExcludedSymbols,
                          ByLibraryVersion<ExcludedSymbol>(libraryId, sourceVersion),
                          item => item with
                                      {
                                          Id = RemapIdSegment(item.Id, segmentIndex: 1, targetVersion),
                                          Version = targetVersion
                                      },
                          item => item.Id,
                          ct) ||
                      await HasMappedTargetIdCollisionAsync(
                          mContext.VersionDiffs,
                          sourceDiffs,
                          item => LibraryRenameMapper.MapVersionDiff(item,
                                                                      libraryId,
                                                                      sourceVersion,
                                                                      targetVersion),
                          item => item.Id,
                          ct);
        return result;
    }

    private static async Task<bool> HasMappedTargetIdCollisionAsync<T>(
        IMongoCollection<T> collection,
        FilterDefinition<T> sourceFilter,
        Func<T, T> mapper,
        Func<T, string> idSelector,
        CancellationToken ct)
    {
        using IAsyncCursor<T> cursor = await collection.FindAsync(sourceFilter, cancellationToken: ct);
        bool result = false;
        while(!result && await cursor.MoveNextAsync(ct))
        {
            foreach(T source in cursor.Current)
            {
                if (!result)
                {
                    string sourceId = idSelector(source);
                    string targetId = idSelector(mapper(source));
                    result = !sourceId.Equals(targetId, StringComparison.Ordinal) &&
                             await ExistsAsync(
                                 collection,
                                 Builders<T>.Filter.Eq(new StringFieldDefinition<T, string>(MongoIdField), targetId),
                                 ct);
                }
            }
        }
        return result;
    }

    private async Task EnsureModeMarkersAsync(LibraryRenameOperationRecord operation, CancellationToken ct)
    {
        IEnumerable<string> ids = operation.Kind == LibraryRenameOperationKind.Library
                                      ? [operation.SourceLibraryId, operation.TargetLibraryId]
                                      : [operation.SourceLibraryId];
        foreach(string libraryId in ids)
        {
            DateTime expectedReservedAtUtc = libraryId == operation.SourceLibraryId
                                                 ? operation.SourceOwnershipReservedAtUtc ??
                                                   throw new InvalidOperationException(
                                                       "The rename operation has no source ownership identity.")
                                                 : operation.TargetOwnershipReservedAtUtc ??
                                                   throw new InvalidOperationException(
                                                       "The rename operation has no target ownership identity.");
            bool owned = await ExistsAsync(
                             mContext.LibraryIngestionModes,
                             Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.Id, libraryId) &
                             Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.Mode,
                                                                            operation.Mode) &
                             Builders<LibraryIngestionModeRecord>.Filter.Eq(
                                 record => record.PendingRenameOperationId,
                                 operation.OperationId) &
                             Builders<LibraryIngestionModeRecord>.Filter.Eq(record => record.ReservedAtUtc,
                                                                            expectedReservedAtUtc) &
                             Builders<LibraryIngestionModeRecord>.Filter.Ne(record => record.LeaseOwnerToken,
                                                                            value: null),
                             ct);
            if (!owned)
                throw new InvalidOperationException(
                    $"Rename operation '{operation.OperationId}' does not own '{libraryId}'.");
        }
    }

    private async Task MarkSourceDirectoryPendingAsync(LibraryRenameOperationRecord operation,
                                                       DirectoryLibraryDefinition source,
                                                       CancellationToken ct)
    {
        if (source.PendingRenameOperationId != operation.OperationId)
        {
            if (source.PendingRenameOperationId != null || source.PublicationLeaseScanRunId != null)
                throw new InvalidOperationException("The source directory definition is busy.");
            FilterDefinition<DirectoryLibraryDefinition> filter =
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.Id, operation.SourceLibraryId) &
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationRevision,
                                                                 operation.SourceRegistrationRevision) &
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.RegistrationIncarnationId,
                                                                 operation.SourceRegistrationIncarnationId) &
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PendingRenameOperationId,
                                                                 value: null) &
                Builders<DirectoryLibraryDefinition>.Filter.Eq(item => item.PublicationLeaseScanRunId,
                                                                 value: null);
            UpdateResult updated = await mContext.DirectoryLibraries.UpdateOneAsync(
                                       filter,
                                       Builders<DirectoryLibraryDefinition>.Update.Set(
                                           item => item.PendingRenameOperationId,
                                           operation.OperationId),
                                       cancellationToken: ct);
            if (updated.ModifiedCount != 1)
                throw new InvalidOperationException("The source directory definition changed before rename began.");
        }
    }

    private async Task ReplaceSourceDocumentsAsync(string libraryId,
                                                   Func<SourceDocumentRecord, SourceDocumentRecord> mapper,
                                                   CancellationToken ct) =>
        await UpsertMappedAsync(mContext.SourceDocuments,
                                Builders<SourceDocumentRecord>.Filter.Eq(document => document.LibraryId,
                                                                          libraryId),
                                mapper,
                                document => document.Id,
                                ct);

    private async Task ReplaceProjectProfilesAsync(string sourceLibraryId,
                                                   string targetLibraryId,
                                                   CancellationToken ct) =>
        await UpsertMappedAsync(mContext.ProjectProfiles,
                                Builders<ProjectProfile>.Filter.AnyEq(profile => profile.IngestedPackages,
                                                                       sourceLibraryId),
                                profile => LibraryRenameMapper.MapProjectProfile(profile,
                                                                                  sourceLibraryId,
                                                                                  targetLibraryId),
                                profile => profile.Id,
                                ct);

    private async Task ReplaceAuditEntriesAsync(string libraryId,
                                                Func<string, string> versionMapper,
                                                string targetLibraryId,
                                                CancellationToken ct) =>
        await UpsertMappedAsync(mContext.ScrapeAuditLog,
                                Builders<ScrapeAuditLogEntry>.Filter.Eq(entry => entry.LibraryId, libraryId),
                                entry => LibraryRenameMapper.MapScrapeAudit(entry,
                                                                            targetLibraryId,
                                                                            versionMapper(entry.Version)),
                                entry => entry.Id,
                                ct);

    private async Task ReplacePreviousVersionReferencesAsync(string libraryId,
                                                             string sourceVersion,
                                                             string targetVersion,
                                                             CancellationToken ct) =>
        await UpsertMappedAsync(mContext.LibraryVersions,
                                Builders<LibraryVersionRecord>.Filter.Eq(item => item.LibraryId, libraryId) &
                                Builders<LibraryVersionRecord>.Filter.Eq(item => item.PreviousVersion,
                                                                          sourceVersion),
                                item => LibraryRenameMapper.MapPreviousVersionReference(item,
                                                                                         sourceVersion,
                                                                                         targetVersion),
                                item => item.Id,
                                ct);

    private async Task DeleteWholeSourceRowsAsync(string sourceId, CancellationToken ct)
    {
        IReadOnlyList<string> shardVersions = await mContext.Bm25Shards
                                                            .Find(item => item.LibraryId == sourceId)
                                                            .Project(item => item.Version)
                                                            .ToListAsync(ct);
        var shards = new Bm25ShardRepository(mContext);
        foreach(string version in shardVersions.Distinct(StringComparer.Ordinal))
            await shards.DeleteAsync(sourceId, version, ct);
        await mContext.LibraryVersions.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.Chunks.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.Pages.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.DocumentRevisions.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.SubjectCatalogs.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.SubjectAssignments.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.LibraryProfiles.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.LibraryIndexes.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.ExcludedSymbols.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.VersionDiffs.DeleteManyAsync(item => item.LibraryId == sourceId, ct);
        await mContext.Libraries.DeleteOneAsync(item => item.Id == sourceId, ct);
    }

    private async Task DeleteVersionSourceRowsAsync(string libraryId,
                                                    string sourceVersion,
                                                    FilterDefinition<VersionDiffRecord> sourceDiffs,
                                                    CancellationToken ct)
    {
        var shards = new Bm25ShardRepository(mContext);
        await shards.DeleteAsync(libraryId, sourceVersion, ct);
        await mContext.LibraryVersions.DeleteManyAsync(ByLibraryVersion<LibraryVersionRecord>(libraryId,
                                                                                               sourceVersion),
                                                        ct);
        await mContext.Chunks.DeleteManyAsync(ByLibraryVersion<DocChunk>(libraryId, sourceVersion), ct);
        await mContext.Pages.DeleteManyAsync(ByLibraryVersion<PageRecord>(libraryId, sourceVersion), ct);
        await mContext.DocumentRevisions.DeleteManyAsync(
            ByLibraryVersion<DocumentRevisionRecord>(libraryId, sourceVersion),
            ct);
        await mContext.SubjectAssignments.DeleteManyAsync(
            ByLibraryVersion<SubjectAssignmentRecord>(libraryId, sourceVersion),
            ct);
        await mContext.LibraryProfiles.DeleteManyAsync(ByLibraryVersion<LibraryProfile>(libraryId,
                                                                                         sourceVersion),
                                                        ct);
        await mContext.LibraryIndexes.DeleteManyAsync(ByLibraryVersion<LibraryIndex>(libraryId,
                                                                                      sourceVersion),
                                                       ct);
        await mContext.ExcludedSymbols.DeleteManyAsync(ByLibraryVersion<ExcludedSymbol>(libraryId,
                                                                                         sourceVersion),
                                                        ct);
        await mContext.VersionDiffs.DeleteManyAsync(sourceDiffs, ct);
    }

    private async Task EnsureWholeSourceRowsRemovedAsync(string sourceId, CancellationToken ct)
    {
        bool remains = await ExistsAsync(mContext.Libraries,
                                         Builders<LibraryRecord>.Filter.Eq(item => item.Id, sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.LibraryVersions,
                                         Builders<LibraryVersionRecord>.Filter.Eq(item => item.LibraryId,
                                                                                   sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.Chunks,
                                         Builders<DocChunk>.Filter.Eq(item => item.LibraryId, sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.Pages,
                                         Builders<PageRecord>.Filter.Eq(item => item.LibraryId, sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.SourceDocuments,
                                         Builders<SourceDocumentRecord>.Filter.Eq(item => item.LibraryId,
                                                                                   sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.DocumentRevisions,
                                         Builders<DocumentRevisionRecord>.Filter.Eq(item => item.LibraryId,
                                                                                     sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.SubjectCatalogs,
                                         Builders<SubjectCatalogRecord>.Filter.Eq(item => item.LibraryId,
                                                                                   sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.SubjectAssignments,
                                         Builders<SubjectAssignmentRecord>.Filter.Eq(item => item.LibraryId,
                                                                                      sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.LibraryProfiles,
                                         Builders<LibraryProfile>.Filter.Eq(item => item.LibraryId, sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.LibraryIndexes,
                                         Builders<LibraryIndex>.Filter.Eq(item => item.LibraryId, sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.Bm25Shards,
                                         Builders<Bm25Shard>.Filter.Eq(item => item.LibraryId, sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.ExcludedSymbols,
                                         Builders<ExcludedSymbol>.Filter.Eq(item => item.LibraryId, sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.VersionDiffs,
                                         Builders<VersionDiffRecord>.Filter.Eq(item => item.LibraryId, sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.Jobs,
                                         Builders<JobRecord>.Filter.Eq(item => item.LibraryId, sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.ScrapeAuditLog,
                                         Builders<ScrapeAuditLogEntry>.Filter.Eq(item => item.LibraryId,
                                                                                 sourceId),
                                         ct) ||
                       await ExistsAsync(mContext.ProjectProfiles,
                                         Builders<ProjectProfile>.Filter.AnyEq(item => item.IngestedPackages,
                                                                                sourceId),
                                         ct);
        if (remains)
            throw new InvalidOperationException($"Source library '{sourceId}' still has authoritative rows.");
    }

    private async Task EnsureVersionSourceRowsRemovedAsync(string libraryId,
                                                           string sourceVersion,
                                                           CancellationToken ct)
    {
        bool remains = await HasVersionSourceRowsAsync(libraryId, sourceVersion, ct);
        if (remains)
            throw new InvalidOperationException(
                $"Source version '{libraryId}/{sourceVersion}' still has authoritative rows.");
    }

    private async Task<RenameLibraryResult> CountLibraryTargetAsync(string libraryId, CancellationToken ct)
    {
        long libraries = await mContext.Libraries.CountDocumentsAsync(item => item.Id == libraryId,
                                                                       cancellationToken: ct);
        long versions = await mContext.LibraryVersions.CountDocumentsAsync(item => item.LibraryId == libraryId,
                                                                            cancellationToken: ct);
        long chunks = await mContext.Chunks.CountDocumentsAsync(item => item.LibraryId == libraryId,
                                                                 cancellationToken: ct);
        long pages = await mContext.Pages.CountDocumentsAsync(item => item.LibraryId == libraryId,
                                                               cancellationToken: ct);
        long profiles = await mContext.LibraryProfiles.CountDocumentsAsync(item => item.LibraryId == libraryId,
                                                                            cancellationToken: ct);
        long indexes = await mContext.LibraryIndexes.CountDocumentsAsync(item => item.LibraryId == libraryId,
                                                                          cancellationToken: ct);
        long shards = await mContext.Bm25Shards.CountDocumentsAsync(item => item.LibraryId == libraryId,
                                                                    cancellationToken: ct);
        long excluded = await mContext.ExcludedSymbols.CountDocumentsAsync(item => item.LibraryId == libraryId,
                                                                            cancellationToken: ct);
        long jobs = await mContext.Jobs.CountDocumentsAsync(item => item.LibraryId == libraryId,
                                                             cancellationToken: ct);
        return new RenameLibraryResult(libraries,
                                       versions,
                                       chunks,
                                       pages,
                                       profiles,
                                       indexes,
                                       shards,
                                       excluded,
                                       jobs);
    }

    private async Task<RenameLibraryResult> CountVersionTargetAsync(string libraryId,
                                                                   string version,
                                                                   CancellationToken ct)
    {
        long libraries = await mContext.Libraries.CountDocumentsAsync(item => item.Id == libraryId,
                                                                       cancellationToken: ct);
        long versions = await mContext.LibraryVersions.CountDocumentsAsync(
                            ByLibraryVersion<LibraryVersionRecord>(libraryId, version),
                            cancellationToken: ct);
        long chunks = await mContext.Chunks.CountDocumentsAsync(ByLibraryVersion<DocChunk>(libraryId, version),
                                                                 cancellationToken: ct);
        long pages = await mContext.Pages.CountDocumentsAsync(ByLibraryVersion<PageRecord>(libraryId, version),
                                                               cancellationToken: ct);
        long profiles = await mContext.LibraryProfiles.CountDocumentsAsync(
                            ByLibraryVersion<LibraryProfile>(libraryId, version),
                            cancellationToken: ct);
        long indexes = await mContext.LibraryIndexes.CountDocumentsAsync(
                           ByLibraryVersion<LibraryIndex>(libraryId, version),
                           cancellationToken: ct);
        long shards = await mContext.Bm25Shards.CountDocumentsAsync(ByLibraryVersion<Bm25Shard>(libraryId, version),
                                                                    cancellationToken: ct);
        long excluded = await mContext.ExcludedSymbols.CountDocumentsAsync(
                            ByLibraryVersion<ExcludedSymbol>(libraryId, version),
                            cancellationToken: ct);
        long jobs = await mContext.Jobs.CountDocumentsAsync(ByLibraryVersion<JobRecord>(libraryId, version),
                                                             cancellationToken: ct);
        return new RenameLibraryResult(libraries,
                                       versions,
                                       chunks,
                                       pages,
                                       profiles,
                                       indexes,
                                       shards,
                                       excluded,
                                       jobs);
    }

    private async Task RetargetDocumentArtifactClaimsAsync(
        FilterDefinition<DocumentRevisionRecord> sourceFilter,
        Func<DocumentRevisionRecord, string> targetRevisionId,
        CancellationToken ct)
    {
        using IAsyncCursor<DocumentRevisionRecord> revisions =
            await mContext.DocumentRevisions.FindAsync(sourceFilter, cancellationToken: ct);
        while(await revisions.MoveNextAsync(ct))
        {
            foreach(DocumentRevisionRecord revision in revisions.Current)
            {
                string targetId = targetRevisionId(revision);
                bool targetExists = await mContext.DocumentRevisions.Find(candidate => candidate.Id == targetId)
                                                  .AnyAsync(ct);
                if (!targetExists)
                    throw new InvalidOperationException(
                        $"Cannot retarget artifact ownership because renamed revision '{targetId}' is missing.");

                foreach(DocumentRevisionArtifactClaim claim in revision.ArtifactClaims.DistinctBy(candidate =>
                            (candidate.ArtifactHash, candidate.ClaimId)))
                {
                    await RetargetDocumentArtifactClaimAsync(claim.ArtifactHash,
                                                             revision.Id,
                                                             targetId,
                                                             claim.ClaimId,
                                                             ct);
                }
            }
        }
    }

    private async Task RetargetDocumentArtifactClaimAsync(string artifactHash,
                                                          string sourceRevisionId,
                                                          string targetRevisionId,
                                                          string claimId,
                                                          CancellationToken ct)
    {
        FilterDefinition<DocumentArtifactClaimRecord> exactSourceClaim =
            Builders<DocumentArtifactClaimRecord>.Filter.And(
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.ClaimId, claimId),
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.RevisionId, sourceRevisionId));
        FilterDefinition<DocumentArtifactClaimRecord> exactTargetClaim =
            Builders<DocumentArtifactClaimRecord>.Filter.And(
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.ClaimId, claimId),
                Builders<DocumentArtifactClaimRecord>.Filter.Eq(claim => claim.RevisionId, targetRevisionId));
        FilterDefinition<DocumentArtifactBlobRecord> artifactFilter =
            Builders<DocumentArtifactBlobRecord>.Filter.And(
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.Id, artifactHash),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.ClaimSchemaVersion,
                                                                 DocumentArtifactBlobRecord
                                                                     .CurrentClaimSchemaVersion),
                Builders<DocumentArtifactBlobRecord>.Filter.Eq(artifact => artifact.DeletionId, value: null),
                Builders<DocumentArtifactBlobRecord>.Filter.ElemMatch(artifact => artifact.Claims,
                                                                       exactSourceClaim),
                Builders<DocumentArtifactBlobRecord>.Filter.Not(
                    Builders<DocumentArtifactBlobRecord>.Filter.ElemMatch(artifact => artifact.Claims,
                                                                           exactTargetClaim)));
        UpdateDefinition<DocumentArtifactBlobRecord> update =
            Builders<DocumentArtifactBlobRecord>.Update.Set(ArtifactClaimRevisionIdFieldPath,
                                                              targetRevisionId);
        await mContext.DocumentArtifactBlobs.UpdateOneAsync(artifactFilter,
                                                            update,
                                                            cancellationToken: ct);

        DocumentArtifactBlobRecord? current = await mContext.DocumentArtifactBlobs
                                                             .Find(artifact => artifact.Id == artifactHash)
                                                             .FirstOrDefaultAsync(ct);
        int targetClaimCount = current?.Claims.Count(claim =>
                                   claim.ClaimId.Equals(claimId, StringComparison.Ordinal) &&
                                   claim.RevisionId.Equals(targetRevisionId, StringComparison.Ordinal)) ?? 0;
        int sourceClaimCount = current?.Claims.Count(claim =>
                                   claim.ClaimId.Equals(claimId, StringComparison.Ordinal) &&
                                   claim.RevisionId.Equals(sourceRevisionId, StringComparison.Ordinal)) ?? 0;
        bool exactlyMoved = current is
                            {
                                ClaimSchemaVersion: DocumentArtifactBlobRecord.CurrentClaimSchemaVersion,
                                DeletionId: null
                            } && targetClaimCount == 1 && sourceClaimCount == 0;
        if (!exactlyMoved)
            throw new InvalidOperationException(
                $"Cannot retarget artifact '{artifactHash}' claim '{claimId}' from revision " +
                $"'{sourceRevisionId}' to '{targetRevisionId}'.");
    }

    private static async Task UpsertMappedAsync<T>(IMongoCollection<T> collection,
                                                   FilterDefinition<T> sourceFilter,
                                                   Func<T, T> mapper,
                                                   Func<T, string> idSelector,
                                                   CancellationToken ct)
    {
        using IAsyncCursor<T> cursor = await collection.FindAsync(sourceFilter, cancellationToken: ct);
        while(await cursor.MoveNextAsync(ct))
        {
            foreach(T source in cursor.Current)
            {
                T target = mapper(source);
                string targetId = idSelector(target);
                FilterDefinition<T> targetFilter = Builders<T>.Filter.Eq(
                    new StringFieldDefinition<T, string>(MongoIdField),
                    targetId);
                await collection.ReplaceOneAsync(targetFilter, target, smUpsertOptions, ct);
            }
        }
    }

    private static async Task<bool> ExistsAsync<T>(IMongoCollection<T> collection,
                                                   FilterDefinition<T> filter,
                                                   CancellationToken ct)
    {
        long count = await collection.CountDocumentsAsync(filter,
                                                           new CountOptions { Limit = 1 },
                                                           ct);
        return count == 1;
    }

    private static FilterDefinition<T> ByLibraryVersion<T>(string libraryId, string version) =>
        Builders<T>.Filter.And(
            Builders<T>.Filter.Eq(new StringFieldDefinition<T, string>(LibraryIdField), libraryId),
            Builders<T>.Filter.Eq(new StringFieldDefinition<T, string>(VersionField), version));

    private static string RemapIdSegment(string id, int segmentIndex, string replacement)
    {
        string[] segments = id.Split('/');
        if (segments.Length <= segmentIndex)
            throw new InvalidOperationException($"Composite identity '{id}' has no segment {segmentIndex}.");
        segments[segmentIndex] = replacement;
        return string.Join('/', segments);
    }

    private static void EnsureExactDirectoryDefinition(DirectoryLibraryDefinition actual,
                                                       DirectoryLibraryDefinition expected,
                                                       string role)
    {
        if (!DirectoryDefinitionsEqual(actual, expected))
            throw new InvalidOperationException(
                $"The {role} directory definition changed before rename completed.");
    }

    private static bool DirectoryDefinitionsEqual(DirectoryLibraryDefinition? actual,
                                                  DirectoryLibraryDefinition? expected)
    {
        bool result = actual != null && expected != null &&
                      actual.Id.Equals(expected.Id, StringComparison.Ordinal) &&
                      actual.RootPath.Equals(expected.RootPath, StringComparison.Ordinal) &&
                      actual.Name == expected.Name &&
                      actual.Hint == expected.Hint &&
                      actual.Recursive == expected.Recursive &&
                      actual.AllowedExtensions.SequenceEqual(expected.AllowedExtensions,
                                                               StringComparer.Ordinal) &&
                      actual.ExclusionPatterns.SequenceEqual(expected.ExclusionPatterns,
                                                               StringComparer.Ordinal) &&
                      actual.BindingStatus == expected.BindingStatus &&
                      actual.RegisteredAtUtc == expected.RegisteredAtUtc &&
                      actual.RegistrationRevision == expected.RegistrationRevision &&
                      actual.RegistrationIncarnationId == expected.RegistrationIncarnationId &&
                      actual.PublicationLeaseScanRunId == expected.PublicationLeaseScanRunId &&
                      actual.PublicationLeaseRegistrationRevision ==
                      expected.PublicationLeaseRegistrationRevision &&
                      actual.PublicationLeaseExpiresAtUtc == expected.PublicationLeaseExpiresAtUtc &&
                      actual.PendingRenameOperationId == expected.PendingRenameOperationId &&
                      actual.LastPublishedAtUtc == expected.LastPublishedAtUtc &&
                      actual.LastPublishedVersion == expected.LastPublishedVersion;
        return result;
    }

    private static void ValidateOperation(LibraryRenameOperationRecord operation,
                                          LibraryRenameOperationKind? expectedKind = null)
    {
        ValidateOperationIdentity(operation);
        if (expectedKind.HasValue && operation.Kind != expectedKind.Value)
            throw new ArgumentException("The durable rename operation does not match the requested mutation.",
                                        nameof(operation));
        if (operation.State != LibraryRenameOperationState.Applying)
            throw new InvalidOperationException("Mongo rename mutations require an Applying operation.");
    }

    private static void ValidateOperationState(LibraryRenameOperationRecord operation,
                                               LibraryRenameOperationState expectedState)
    {
        ValidateOperationIdentity(operation);
        if (operation.State != expectedState)
            throw new ArgumentException("The durable rename operation is not at the required checkpoint.",
                                        nameof(operation));
    }

    private static void ValidateOperationIdentity(LibraryRenameOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
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
        if (!operation.Id.Equals(operation.SourceLibraryId, StringComparison.Ordinal) ||
            operation.SourceOwnershipReservedAtUtc == null ||
            operation.TargetOwnershipReservedAtUtc == null ||
            !validShape)
            throw new ArgumentException("The durable rename operation has an invalid identity shape.",
                                        nameof(operation));
        bool directorySnapshotsValid = operation.Mode == LibraryIngestionMode.Directory
                                           ? operation.SourceDirectorySnapshot != null &&
                                             operation.TargetDirectorySnapshot != null
                                           : operation.SourceDirectorySnapshot == null &&
                                             operation.TargetDirectorySnapshot == null;
        if (!directorySnapshotsValid)
            throw new ArgumentException("The durable rename operation has invalid directory snapshots.",
                                        nameof(operation));
    }

    private static readonly ReplaceOptions smUpsertOptions = new() { IsUpsert = true };
    private const string LibraryIdField = "LibraryId";
    private const string VersionField = "Version";
    private const string MongoIdField = "_id";
    private const string ArtifactClaimRevisionIdFieldPath = "Claims.$.RevisionId";
    private const string SourceRole = "source";
}
