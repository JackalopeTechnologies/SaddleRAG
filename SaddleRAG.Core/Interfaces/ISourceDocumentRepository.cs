// ISourceDocumentRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Owns local-document identity, revision metadata, and artifact lifetime.
/// </summary>
public interface ISourceDocumentRepository
{
    Task UpsertDirectoryDefinitionAsync(DirectoryLibraryDefinition definition,
                                        CancellationToken ct = default);

    Task<DirectoryLibraryDefinition> RegisterDirectoryDefinitionAsync(
        DirectoryLibraryDefinition definition,
        CancellationToken ct = default);

    Task<DirectoryLibraryDefinition?> GetDirectoryDefinitionAsync(string libraryId,
                                                                   CancellationToken ct = default);

    Task<IReadOnlyList<DirectoryLibraryDefinition>> GetDirectoryDefinitionsAsync(
        CancellationToken ct = default);

    Task<IDirectoryPublicationLease?> TryAcquireDirectoryPublicationLeaseAsync(
        string libraryId,
        long registrationRevision,
        string? registrationIncarnationId,
        string scanRunId,
        string? expectedPublishedVersion,
        CancellationToken ct = default);

    Task<bool> TryUpdateDirectoryPublicationAsync(IDirectoryPublicationLease lease,
                                                  string? expectedPublishedVersion,
                                                  DateTime? publishedAtUtc,
                                                  string? publishedVersion,
                                                  CancellationToken ct = default);

    Task<bool> TryApplyDirectoryPackagePublicationAsync(
        IDirectoryPublicationLease lease,
        string? expectedPublishedVersion,
        DirectoryLibraryDefinition packageDefinition,
        DateTime publishedAtUtc,
        string publishedVersion,
        CancellationToken ct = default);

    Task<bool> TryRestoreDirectoryPublicationAsync(IDirectoryPublicationLease lease,
                                                   string failedPublishedVersion,
                                                   DateTime? restoredPublishedAtUtc,
                                                   string? restoredPublishedVersion,
                                                   CancellationToken ct = default);

    Task<bool> TryDeleteLeasedDirectoryDefinitionAsync(IDirectoryPublicationLease lease,
                                                        CancellationToken ct = default);

    Task<SourceDocumentRecord> GetOrCreateDocumentAsync(SourceDocumentRecord candidate,
                                                         CancellationToken ct = default);

    Task<SourceDocumentRecord?> GetDocumentAsync(string documentId, CancellationToken ct = default);

    Task PersistRevisionAsync(DocumentRevisionRecord revision,
                              Stream originalArtifact,
                              Stream? extractionArtifact,
                              CancellationToken ct = default);

    Task<DocumentRevisionRecord?> GetRevisionAsync(string revisionId, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentRevisionRecord>> GetRevisionsAsync(string libraryId,
                                                                   string version,
                                                                   CancellationToken ct = default);

    Task<IReadOnlyList<DocumentRevisionRecord>> GetRevisionsAsync(string libraryId,
                                                                   CancellationToken ct = default);

    Task<IReadOnlyList<LibraryVersionKey>> GetDistinctLibraryVersionPairsAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetArtifactHashesBecomingUnreferencedAsync(
        IReadOnlyCollection<string> deletingRevisionIds,
        CancellationToken ct = default);

    Task<Stream> OpenArtifactAsync(string sha256, CancellationToken ct = default);

    Task<bool> DeleteRevisionAsync(string revisionId, CancellationToken ct = default);

    Task<DocumentArtifactRecoveryResult> RecoverArtifactClaimsAsync(DateTime utcNow,
                                                                     CancellationToken ct = default);

    Task<long> DeleteCandidateScanRunAsync(string libraryId,
                                           string scanRunId,
                                           CancellationToken ct = default);

    Task<long> DeleteVersionAsync(string libraryId, string version, CancellationToken ct = default);

    Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default);

    Task<long> SetRevisionStateAsync(string libraryId,
                                     string version,
                                     DocumentRevisionState state,
                                     CancellationToken ct = default);

    Task<long> PublishCandidateScanRunAsync(string libraryId,
                                            string version,
                                            string scanRunId,
                                            CancellationToken ct = default);
}
