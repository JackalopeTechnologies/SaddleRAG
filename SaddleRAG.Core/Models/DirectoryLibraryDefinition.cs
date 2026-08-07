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

    public DateTime? LastPublishedAtUtc { get; init; }

    public string? LastPublishedVersion { get; init; }
}
