// DirectoryScanReport.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Sanitized result of one manually invoked directory preview.</summary>
public sealed record DirectoryScanReport
{
    public required string LibraryId { get; init; }

    public required string ScanRunId { get; init; }

    public required DirectoryScanStatus Status { get; init; }

    public required string ReasonCode { get; init; }

    public required string Detail { get; init; }

    public required DateTime StartedAtUtc { get; init; }

    public required DateTime CompletedAtUtc { get; init; }

    public required IReadOnlyList<DirectoryScanEntryResult> Entries { get; init; }

    public int DiscoveredCount { get; init; }

    public int ExtractedCount { get; init; }

    public int SkippedCount { get; init; }

    public int FailedCount { get; init; }
}
