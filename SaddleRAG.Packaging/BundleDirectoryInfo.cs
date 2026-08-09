// BundleDirectoryInfo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Serialization;

namespace SaddleRAG.Packaging;

/// <summary>Portable directory scan options with no machine-local binding.</summary>
public sealed record BundleDirectoryInfo
{
    [JsonPropertyName("recursive")]
    public bool Recursive { get; init; }

    [JsonPropertyName("allowedExtensions")]
    public IReadOnlyList<string> AllowedExtensions { get; init; } = [];

    [JsonPropertyName("exclusionPatterns")]
    public IReadOnlyList<string> ExclusionPatterns { get; init; } = [];
}
