// SubjectSelection.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>One subject selected for a document with supporting evidence.</summary>
public sealed record SubjectSelection
{
    public required string SubjectId { get; init; }

    public required float Confidence { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];
}
