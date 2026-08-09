// ISubjectAssignmentRepository.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Core.Interfaces;

#pragma warning disable STR0010 // Interface methods cannot validate parameters

/// <summary>Persistence contract for document subject assignments.</summary>
public interface ISubjectAssignmentRepository
{
    Task PersistAsync(SubjectAssignmentRecord assignment, CancellationToken ct = default);

    Task<IReadOnlyList<SubjectAssignmentRecord>> GetByDocumentRevisionIdsAsync(
        IReadOnlyCollection<string> documentRevisionIds,
        CancellationToken ct = default);

    Task<long> DeleteScanRunAsync(string libraryId, string scanRunId, CancellationToken ct = default);

    Task<long> DeleteVersionAsync(string libraryId, string version, CancellationToken ct = default);

    Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default);
}
