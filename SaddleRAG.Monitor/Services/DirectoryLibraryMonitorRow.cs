// DirectoryLibraryMonitorRow.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Monitor.Services;

/// <summary>Operator-facing projection of one manually registered directory library.</summary>
public sealed record DirectoryLibraryMonitorRow
{
    public required string LibraryId { get; init; }

    public required string Name { get; init; }

    public required string Hint { get; init; }

    public required string RootPath { get; init; }

    public bool Recursive { get; init; }

    public IReadOnlyList<string> AllowedExtensions { get; init; } = [];

    public IReadOnlyList<string> ExclusionPatterns { get; init; } = [];

    public DirectoryLibraryBindingStatus BindingStatus { get; init; }

    public string? LastSuccessfulVersion { get; init; }

    public DateTime? LastSuccessfulAt { get; init; }

    public string? LatestJobId { get; init; }

    public string? LatestJobStatus { get; init; }

    /// <summary>When the latest scan started running, or null while it is still queued.</summary>
    public DateTime? LatestJobStartedAt { get; init; }

    /// <summary>
    ///     When the latest scan last moved. A running job whose last progress is old is
    ///     stuck, which "Running" alone never showed.
    /// </summary>
    public DateTime? LatestJobLastProgressAt { get; init; }

    /// <summary>Why the latest scan stopped, when it stopped badly.</summary>
    public string? LatestJobError { get; init; }

    public DirectoryScanJobProgress? Progress { get; init; }

    public IReadOnlyList<DirectoryScanFileFailure> FileFailures { get; init; } = [];
}
