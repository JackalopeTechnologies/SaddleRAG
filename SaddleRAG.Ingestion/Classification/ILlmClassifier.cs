// ILlmClassifier.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Minimal seam for the streaming-pipeline classify stage and the
///     <c>saddlerag-cli reclassify</c> command: single per-page
///     classification call, plus backend-identity members so downstream
///     code can record and report which backend and model produced a
///     classification. Public so the CLI handler (in <c>SaddleRAG.Cli</c>)
///     can take this seam for unit testing without dragging in the
///     OllamaSharp client or the ONNX native runtime.
/// </summary>
public interface ILlmClassifier
{
    /// <summary>
    ///     Name of the backend that fulfils <see cref="ClassifyAsync" />:
    ///     <c>"onnx"</c> or <c>"ollama"</c>.
    /// </summary>
    string BackendName { get; }

    /// <summary>
    ///     Model identifier reported by this backend (e.g.
    ///     "phi-3-mini-4k-instruct-directml" or "phi4-mini").
    /// </summary>
    string ModelId { get; }

    /// <summary>
    ///     Classify <paramref name="page" /> for the library identified by
    ///     <paramref name="libraryHint" />. Returns <see cref="DocCategory.Unclassified" />
    ///     with zero confidence when the LLM declines to classify.
    /// </summary>
    Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                  string libraryHint,
                                                                  CancellationToken ct = default);

    /// <summary>
    ///     Version string combining model id and prompt version. Used by
    ///     reextract to decide whether a re-classification is needed and to
    ///     stamp the library manifest.
    /// </summary>
    string GetCurrentVersion();
}
