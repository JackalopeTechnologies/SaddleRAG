// DirectoryLibraryEditModel.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Monitor.Pages;

/// <summary>Editable values for one explicit directory registration.</summary>
public sealed class DirectoryLibraryEditModel
{
    public string LibraryId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Hint { get; set; } = string.Empty;

    public string RootPath { get; set; } = string.Empty;

    public bool Recursive { get; set; } = true;

    public IReadOnlyList<string> AllowedExtensions { get; set; } =
        DirectoryScanLimits.SupportedExtensions;

    public IReadOnlyList<string> ExclusionPatterns { get; set; } = [];
}
