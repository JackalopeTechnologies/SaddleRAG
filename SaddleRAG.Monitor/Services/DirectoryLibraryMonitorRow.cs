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

    public DirectoryScanJobProgress? Progress { get; init; }

    public IReadOnlyList<DirectoryScanFileFailure> FileFailures { get; init; } = [];
}
