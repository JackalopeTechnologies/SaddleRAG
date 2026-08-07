// DocumentIntakeLimits.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Intake;

/// <summary>Bounds applied to normalized extraction sections.</summary>
public sealed record DocumentIntakeLimits
{
    public int MaxSectionCharacters { get; init; } = DefaultMaxSectionCharacters;

    public const int DefaultMaxSectionCharacters = 200000;
}
