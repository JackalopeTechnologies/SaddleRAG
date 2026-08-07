// DirectoryScanJobProgress.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Sanitized progress for one explicitly queued directory scan. Paths
///     are always relative to the registered root; absolute roots are never
///     persisted or returned through job status.
/// </summary>
public sealed record DirectoryScanJobProgress
{
    public int FilesDiscovered { get; init; }

    public int SupportedDocuments { get; init; }

    public int DocumentsCompleted { get; init; }

    public string? CurrentRelativePath { get; init; }
}
