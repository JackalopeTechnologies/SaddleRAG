// WebIngestionOwnership.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Validates durable source data before committing an acquired web-ingestion lease.</summary>
public static class WebIngestionOwnership
{
    /// <summary>
    ///     Rejects directory registration and unclassified partial data before web ingestion mutates content.
    ///     The caller must hold the lease through its subsequent mutations and dispose it afterward.
    /// </summary>
    public static async Task CommitAsync(RepositoryFactory repositoryFactory,
                                           string? profile,
                                           string libraryId,
                                           ILibraryIngestionModeLease modeLease,
                                           CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentNullException.ThrowIfNull(modeLease);

        ISourceDocumentRepository sources = repositoryFactory.GetSourceDocumentRepository(profile);
        DirectoryLibraryDefinition? definition = await sources.GetDirectoryDefinitionAsync(libraryId, ct);
        if (definition != null)
        {
            if (modeLease.OwnershipStateAtAcquisition == LibraryIngestionOwnershipState.Reserved)
                await modeLease.TryReconcileReservedModeAsync(LibraryIngestionMode.Directory,
                                                               CancellationToken.None);
            throw new InvalidOperationException(
                $"Library '{libraryId}' is registered for directory ingestion and cannot be mutated by web ingestion.");
        }

        if (modeLease.OwnershipStateAtAcquisition == LibraryIngestionOwnershipState.Reserved)
        {
            ILibraryIngestionModeRepository modes = repositoryFactory.GetLibraryIngestionModeRepository(profile);
            LibraryIngestionDataEvidence evidence = await modes.GetLibraryDataEvidenceAsync(libraryId, ct);
            if (evidence.HasDirectoryDefinition)
            {
                bool reconciled = await modeLease.TryReconcileReservedModeAsync(LibraryIngestionMode.Directory, ct);
                if (!reconciled)
                    throw new InvalidOperationException(OwnershipLostMessage);
                throw new InvalidOperationException(
                    $"Library '{libraryId}' contains directory-ingestion data and cannot be mutated by web ingestion.");
            }

            // Reconnaissance configuration may precede the first scrape. Acquired content and
            // document lifecycle state require an existing parent to establish web ownership.
            if (!evidence.HasLibraryRecord &&
                (evidence.HasDocumentLifecycleData || evidence.HasChildContentData))
                throw new InvalidOperationException(
                    $"Library '{libraryId}' has unclassified partial data and cannot be mutated by web ingestion.");
        }

        bool committed = await modeLease.TryCommitAsync(ct);
        if (!committed)
            throw new InvalidOperationException(OwnershipLostMessage);
    }

    private const string OwnershipLostMessage = "The web ingestion operation no longer owns its source-mode lease.";
}
