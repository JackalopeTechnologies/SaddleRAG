// SubjectClassifierProvenance.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Identifies the classifier and prompt that produced subject metadata.</summary>
public sealed record SubjectClassifierProvenance
{
    public required string Backend { get; init; }

    public required string ModelId { get; init; }

    public required string PromptVersion { get; init; }

    public required DateTime GeneratedAtUtc { get; init; }
}
