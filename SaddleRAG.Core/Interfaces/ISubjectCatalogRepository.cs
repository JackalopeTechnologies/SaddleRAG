// ISubjectCatalogRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Core.Interfaces;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

/// <summary>Persistence contract for immutable subject catalog revisions.</summary>
public interface ISubjectCatalogRepository
{
    Task<SubjectCatalogRecord?> GetLatestAsync(string libraryId, CancellationToken ct = default);

    Task<SubjectCatalogRecord?> GetAsync(string libraryId,
                                         string taxonomyVersion,
                                         CancellationToken ct = default);

    Task<IReadOnlyList<SubjectCatalogRecord>> GetManyAsync(IReadOnlyCollection<SubjectCatalogKey> keys,
                                                            CancellationToken ct = default);

    Task InsertRevisionAsync(SubjectCatalogRecord catalog, CancellationToken ct = default);

    Task<bool> TryPublishImportCandidateAsync(string libraryId,
                                               string taxonomyVersion,
                                               string importOperationId,
                                               CancellationToken ct = default);

    Task<bool> TryRollbackImportCandidatePublicationAsync(string libraryId,
                                                           string taxonomyVersion,
                                                           string importOperationId,
                                                           CancellationToken ct = default);

    Task<ImportCatalogRollbackOutcome> TryRollbackImportCandidatePublicationIfUnreferencedAsync(
        string libraryId,
        string taxonomyVersion,
        string importOperationId,
        CancellationToken ct = default);

    Task<bool> DeleteImportCandidateIfUnreferencedAsync(string libraryId,
                                                         string taxonomyVersion,
                                                         string importOperationId,
                                                         string deletingVersion,
                                                         CancellationToken ct = default);

    Task<bool> TryPublishCandidateAsync(string libraryId,
                                        string taxonomyVersion,
                                        string scanRunId,
                                        CancellationToken ct = default);

    Task<bool> TryRollbackCandidatePublicationAsync(string libraryId,
                                                     string taxonomyVersion,
                                                     string scanRunId,
                                                     CancellationToken ct = default);

    Task<long> DeleteCandidateScanRunAsync(string libraryId,
                                           string scanRunId,
                                           string? deletingVersion,
                                           CancellationToken ct = default);

    Task<bool> DeleteIfUnreferencedAsync(string libraryId,
                                         string taxonomyVersion,
                                         string deletingVersion,
                                         CancellationToken ct = default);

    Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default);
}
