// ImportResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Packaging;

/// <summary>
///     Outcome of a completed bundle import operation.
/// </summary>
public sealed record ImportResult
{
    public required IReadOnlyList<string> VersionsImported { get; init; }
    public required IReadOnlyList<string> OverwrittenVersions { get; init; }
    public required long BytesFreed { get; init; }
    public required IReadOnlyList<string> PendingReembedJobIds { get; init; }
    public required IReadOnlyList<ImportPartialFailure> PartialFailures { get; init; }
    public required string RecommendedFollowUp { get; init; }
}
