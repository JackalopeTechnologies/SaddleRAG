// SubjectSearchContext.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Resolved explicit filter and inferred boost for one query.</summary>
public sealed record SubjectSearchContext
{
    public string? ExplicitSubjectId { get; init; }

    public string? InferredSubjectId { get; init; }

    public SubjectCatalogRecord? Catalog { get; init; }
}
