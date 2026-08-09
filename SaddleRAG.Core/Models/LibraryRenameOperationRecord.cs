// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Durable recovery record for a library or version rename. The source
///     library identifier is the Mongo identity so only one rename can own a
///     source library at a time.
/// </summary>
public sealed record LibraryRenameOperationRecord
{
    public required string Id { get; init; }

    public required string OperationId { get; init; }

    public required LibraryRenameOperationKind Kind { get; init; }

    public required LibraryRenameOperationState State { get; init; }

    public required LibraryIngestionMode Mode { get; init; }

    public required string SourceLibraryId { get; init; }

    public required string TargetLibraryId { get; init; }

    public DateTime? SourceOwnershipReservedAtUtc { get; init; }

    public DateTime? TargetOwnershipReservedAtUtc { get; init; }

    public string? SourceVersion { get; init; }

    public string? TargetVersion { get; init; }

    public long? SourceRegistrationRevision { get; init; }

    public string? SourceRegistrationIncarnationId { get; init; }

    public string? SourceLastPublishedVersion { get; init; }

    public string? TargetRegistrationIncarnationId { get; init; }

    public DirectoryLibraryDefinition? SourceDirectorySnapshot { get; init; }

    public DirectoryLibraryDefinition? TargetDirectorySnapshot { get; init; }

    public RenameLibraryResult? Counts { get; init; }

    public required DateTime StartedAtUtc { get; init; }

    public required DateTime UpdatedAtUtc { get; init; }
}
