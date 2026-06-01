// ExportResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging;

/// <summary>
///     Outcome of a completed library export operation.
/// </summary>
public sealed record ExportResult
{
    public required string OutputPath { get; init; }
    public required long BytesWritten { get; init; }
    public required IReadOnlyList<string> VersionsExported { get; init; }
    public required int TotalPages { get; init; }
    public required int TotalChunks { get; init; }
}
