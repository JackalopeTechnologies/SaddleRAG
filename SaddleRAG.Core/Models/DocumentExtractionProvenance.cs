// DocumentExtractionProvenance.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Reproducibility metadata for the structured artifact produced from a
///     source document.
/// </summary>
public record DocumentExtractionProvenance
{
    public required string ExtractorName { get; init; }

    public required string ExtractorVersion { get; init; }

    public string? ConfigurationHash { get; init; }

    public bool UsedOcr { get; init; }

    public double? QualityScore { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
