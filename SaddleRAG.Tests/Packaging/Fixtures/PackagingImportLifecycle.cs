// PackagingImportLifecycle.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Tests.Packaging.Fixtures;

internal sealed record PackagingImportLifecycle(
    ILibraryDeletionService DeletionService,
    ILibraryIngestionModeLeaseManager ModeLeaseManager,
    ILibraryIngestionModeRepository ModeRepository)
{
    internal static PackagingImportLifecycle Create(
        ILibraryRepository libraries,
        ILibraryProfileRepository profiles,
        ILibraryIndexRepository indexes,
        IExcludedSymbolsRepository excludedSymbols,
        IDiffRepository diffs,
        IPageRepository pages,
        IChunkRepository chunks,
        IBm25ShardRepository bm25Shards)
    {
        var deletion = Substitute.For<ILibraryDeletionService>();
        deletion.DeleteVersionUnderModeLeaseAsync(Arg.Any<string?>(),
                                                   Arg.Any<string>(),
                                                   Arg.Any<string>(),
                                                   Arg.Any<ILibraryIngestionModeLease>(),
                                                   Arg.Any<CancellationToken>())
                .Returns(call => DeleteVersionAsync(libraries,
                                                    profiles,
                                                    indexes,
                                                    excludedSymbols,
                                                    diffs,
                                                    pages,
                                                    chunks,
                                                    bm25Shards,
                                                    call.ArgAt<string>(1),
                                                    call.ArgAt<string>(2),
                                                    call.ArgAt<CancellationToken>(4)));

        var modes = Substitute.For<ILibraryIngestionModeRepository>();
        modes.GetLibraryDataEvidenceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(call => GetEvidenceAsync(libraries,
                                               call.ArgAt<string>(0),
                                               call.ArgAt<CancellationToken>(1)));

        var manager = Substitute.For<ILibraryIngestionModeLeaseManager>();
        manager.TryAcquireAsync(Arg.Any<string?>(),
                                Arg.Any<string>(),
                                LibraryIngestionMode.Web,
                                Arg.Any<CancellationToken>())
               .Returns(call => CreateLease(call.ArgAt<string>(1)));

        return new PackagingImportLifecycle(deletion, manager, modes);
    }

    private static ILibraryIngestionModeLease CreateLease(string libraryId)
    {
        var lease = Substitute.For<ILibraryIngestionModeLease>();
        lease.LibraryId.Returns(libraryId);
        lease.Mode.Returns(LibraryIngestionMode.Web);
        lease.OwnershipStateAtAcquisition.Returns(LibraryIngestionOwnershipState.Reserved);
        lease.OwnershipLostToken.Returns(CancellationToken.None);
        lease.TryRenewAsync(Arg.Any<CancellationToken>()).Returns(true);
        lease.TryCommitAsync(Arg.Any<CancellationToken>()).Returns(true);
        lease.TryAbandonReservationAsync(Arg.Any<CancellationToken>()).Returns(true);
        lease.TryDeleteOwnershipAsync(Arg.Any<CancellationToken>()).Returns(true);
        return lease;
    }

    private static async Task<LibraryIngestionDataEvidence> GetEvidenceAsync(
        ILibraryRepository libraries,
        string libraryId,
        CancellationToken ct)
    {
        LibraryRecord? library = await libraries.GetLibraryAsync(libraryId, ct);
        return new LibraryIngestionDataEvidence(library != null, false, false, false, false);
    }

    private static async Task<LibraryDeletionResult> DeleteVersionAsync(
        ILibraryRepository libraries,
        ILibraryProfileRepository profiles,
        ILibraryIndexRepository indexes,
        IExcludedSymbolsRepository excludedSymbols,
        IDiffRepository diffs,
        IPageRepository pages,
        IChunkRepository chunks,
        IBm25ShardRepository bm25Shards,
        string libraryId,
        string version,
        CancellationToken ct)
    {
        IReadOnlyList<Bm25Shard> shards = await bm25Shards.GetAllShardsAsync(libraryId, version, ct);
        IEnumerable<string> blobIds = shards
                                      .SelectMany(shard => shard.ExternalTerms.Values
                                                                .Append(shard.ShardGridFsRef))
                                      .OfType<string>()
                                      .Where(id => !string.IsNullOrEmpty(id))
                                      .Distinct(StringComparer.Ordinal);
        foreach(string blobId in blobIds)
            await bm25Shards.DeleteGridFsBlobAsync(blobId, ct);

        long chunkCount = await chunks.DeleteChunksAsync(libraryId, version, ct);
        long pageCount = await pages.DeleteAsync(libraryId, version, ct);
        long profileCount = await profiles.DeleteAsync(libraryId, version, ct);
        long indexCount = await indexes.DeleteAsync(libraryId, version, ct);
        long bm25Count = await bm25Shards.DeleteAsync(libraryId, version, ct);
        long excludedCount = await excludedSymbols.DeleteAsync(libraryId, version, ct);
        long diffCount = await diffs.DeleteVersionAsync(libraryId, version, ct);
        DeleteVersionResult? metadata = await libraries.DeleteVersionAsync(libraryId, version, ct);
        return new LibraryDeletionResult(metadata?.LibraryRowDeleted == true ? 1 : 0,
                                         metadata?.VersionsDeleted ?? 0,
                                         chunkCount,
                                         pageCount,
                                         profileCount,
                                         indexCount,
                                         bm25Count,
                                         excludedCount,
                                         AuditEntries: 0,
                                         CurrentVersionRepointedTo: metadata?.CurrentVersionRepointedTo,
                                         Diffs: diffCount);
    }
}
