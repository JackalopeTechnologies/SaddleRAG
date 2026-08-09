// DirectoryLibraryDefinition.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     User-selected local directory registered as a SaddleRAG library.
///     The absolute directory root is stored only on this definition.
/// </summary>
public record DirectoryLibraryDefinition
{
    public required string Id { get; init; }

    public required string RootPath { get; init; }

    public string? Name { get; init; }

    public string? Hint { get; init; }

    public bool Recursive { get; init; }

    public IReadOnlyList<string> AllowedExtensions { get; init; } = [];

    public IReadOnlyList<string> ExclusionPatterns { get; init; } = [];

    public DirectoryLibraryBindingStatus BindingStatus { get; init; } = DirectoryLibraryBindingStatus.Bound;

    public required DateTime RegisteredAtUtc { get; init; }

    /// <summary>
    ///     Monotonically increasing revision of the user-selected binding and
    ///     scan options. Legacy definitions start at zero.
    /// </summary>
    public long RegistrationRevision { get; init; }

    /// <summary>
    ///     Unique identity of this registration incarnation. A new value is
    ///     assigned whenever the definition is registered or replaced so a
    ///     queued operation cannot match a deleted-and-recreated definition
    ///     whose numeric registration revision happens to be the same.
    ///     Legacy definitions can be null.
    /// </summary>
    public string? RegistrationIncarnationId { get; init; }

    /// <summary>
    ///     Operation that currently owns the durable directory lifecycle
    ///     lease. Existing BSON keeps the publication-era field name. Null
    ///     when registration or deletion may begin.
    /// </summary>
    public string? PublicationLeaseScanRunId { get; init; }

    /// <summary>
    ///     Registration revision captured by the active lifecycle lease.
    /// </summary>
    public long? PublicationLeaseRegistrationRevision { get; init; }

    /// <summary>
    ///     Recovery deadline for an abandoned lifecycle lease. The current
    ///     owner must renew before this deadline; an expired lease is fenced.
    /// </summary>
    public DateTime? PublicationLeaseExpiresAtUtc { get; init; }

    /// <summary>
    ///     Exact durable rename operation that has made this definition
    ///     unavailable. Null only when normal directory operations may use it.
    /// </summary>
    public string? PendingRenameOperationId { get; init; }

    public DateTime? LastPublishedAtUtc { get; init; }

    public string? LastPublishedVersion { get; init; }
}
