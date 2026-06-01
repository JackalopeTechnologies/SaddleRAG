// ImportRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging;

/// <summary>
///     Parameters for a bundle import operation.
/// </summary>
public sealed record ImportRequest
{
    public required string BundlePath { get; init; }
    public bool Overwrite { get; init; }
    public bool Compact { get; init; }

    /// <summary>
    ///     Optional database profile name. When <see cref="Compact" /> is
    ///     <see langword="true" /> and an overwrite occurred, this profile is
    ///     used to resolve the <see cref="MongoDB.Driver.IMongoDatabase" />
    ///     passed to <see cref="ICollectionCompactor.CompactAsync" />.
    ///     <see langword="null" /> uses the default profile.
    /// </summary>
    public string? Profile { get; init; }
}
