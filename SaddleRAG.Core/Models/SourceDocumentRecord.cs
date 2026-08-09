// SourceDocumentRecord.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Stable identity and display metadata for one file beneath a registered
///     directory. Content changes create revisions without changing this id.
/// </summary>
public record SourceDocumentRecord
{
    public required string Id { get; init; }

    public required string LibraryId { get; init; }

    public required string NormalizedRelativePath { get; init; }

    public required string DisplayRelativePath { get; init; }

    public required string DisplayName { get; init; }

    public required string SourceUri { get; init; }

    public required string MediaType { get; init; }

    public required string FirstSeenVersion { get; init; }

    public required DateTime CreatedAtUtc { get; init; }

    public string? LastSeenVersion { get; init; }

    public DateTime? UpdatedAtUtc { get; init; }
}
