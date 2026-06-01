// ExportRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging;

/// <summary>
///     Parameters for a library export operation.
/// </summary>
public sealed record ExportRequest
{
    public required string LibraryId { get; init; }
    public required VersionFilter Versions { get; init; }
    public required string OutputPath { get; init; }
}
