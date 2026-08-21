// ISubjectClassifier.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Assigns catalog subjects to a document and persists the result.</summary>
public interface ISubjectClassifier
{
    Task<SubjectAssignmentRecord> ClassifyAsync(ISubjectAssignmentRepository repository,
                                                SubjectDescriptor descriptor,
                                                SubjectCatalogRecord catalog,
                                                string version,
                                                string scanRunId,
                                                CancellationToken ct = default);

    /// <summary>
    ///     Persists a deterministic, review-flagged fallback assignment (the
    ///     catalog's first concept) for a document whose classification could not
    ///     be parsed, so a completed scan still publishes instead of aborting.
    /// </summary>
    Task<SubjectAssignmentRecord> AssignFallbackAsync(ISubjectAssignmentRepository repository,
                                                      SubjectDescriptor descriptor,
                                                      SubjectCatalogRecord catalog,
                                                      string version,
                                                      string scanRunId,
                                                      CancellationToken ct = default);
}
