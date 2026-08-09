// SubjectSearchPresentation.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Search-facing label, role, confidence, and evidence.</summary>
public sealed record SubjectSearchPresentation
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string Role { get; init; }

    public required float Confidence { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];
}
