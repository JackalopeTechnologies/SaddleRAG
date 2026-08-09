// SubjectAssignmentRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Version-scoped multi-label subject assignment for one document revision.</summary>
public sealed record SubjectAssignmentRecord
{
    public required string Id { get; init; }

    public required string LibraryId { get; init; }

    public required string Version { get; init; }

    public required string ScanRunId { get; init; }

    public required string DocumentId { get; init; }

    public required string DocumentRevisionId { get; init; }

    public required string TaxonomyVersion { get; init; }

    public required SubjectSelection Primary { get; init; }

    public IReadOnlyList<SubjectSelection> Secondary { get; init; } = [];

    public required bool NeedsReview { get; init; }

    public required SubjectClassifierProvenance Provenance { get; init; }
}
