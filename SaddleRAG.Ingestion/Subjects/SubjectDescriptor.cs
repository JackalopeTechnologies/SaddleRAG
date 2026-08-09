// SubjectDescriptor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Bounded document evidence supplied to subject classification.</summary>
public sealed record SubjectDescriptor
{
    public required string DocumentId { get; init; }

    public required string DocumentRevisionId { get; init; }

    public required string Title { get; init; }

    public required string RelativePath { get; init; }

    public IReadOnlyList<string> Headings { get; init; } = [];

    public required string TableOfContents { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<string> StratifiedSections { get; init; } = [];
}
