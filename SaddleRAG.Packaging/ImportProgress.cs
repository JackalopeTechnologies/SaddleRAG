// ImportProgress.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging;

/// <summary>
///     Progress snapshot emitted during a bundle import operation.
/// </summary>
public sealed record ImportProgress
{
    public required string CurrentVersion { get; init; }
    public required string CurrentStep { get; init; }
    public required int VersionIndex { get; init; }
    public required int TotalVersions { get; init; }
}
