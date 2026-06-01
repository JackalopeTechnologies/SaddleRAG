// AuditPageOutcome.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Audit;

/// <summary>
///     Outcome of fetching and indexing a page.
/// </summary>
public sealed record AuditPageOutcome
{
    /// <summary>
    ///     HTTP status code or fetch error label (e.g. "200", "Timeout").
    /// </summary>
    public string? FetchStatus { get; init; }

    /// <summary>
    ///     Classified documentation category of the page.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    ///     Classifier backend that produced <see cref="Category" />
    ///     ("onnx" or "ollama"). Null on audit rows written before provenance.
    /// </summary>
    public string? ClassifierBackend { get; init; }

    /// <summary>
    ///     Classifier model id that produced <see cref="Category" />. Null on
    ///     older audit rows.
    /// </summary>
    public string? ClassifierModel { get; init; }

    /// <summary>
    ///     Number of chunks produced from this page.
    /// </summary>
    public int? ChunkCount { get; init; }

    /// <summary>
    ///     Error message if the page failed to process.
    /// </summary>
    public string? Error { get; init; }
}
