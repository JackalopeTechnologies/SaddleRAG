// SubjectConcept.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>One stable subject concept in a library-scoped taxonomy.</summary>
public sealed record SubjectConcept
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public required string Description { get; init; }
}
