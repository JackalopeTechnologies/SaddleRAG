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
///     classification call. The concrete <see cref="LlmClassifier" /> also
///     exposes <c>GetCurrentVersion</c>; that stays on the concrete type
///     because only <see cref="Recon.RescrubService" /> needs it. Public
///     so the CLI handler (in <c>SaddleRAG.Cli</c>) can take this seam
///     for unit testing without dragging in the OllamaSharp client.
/// </summary>
public interface ILlmClassifier
{
    /// <summary>
    ///     Classify <paramref name="page" /> for the library identified by
    ///     <paramref name="libraryHint" />. Returns <see cref="DocCategory.Unclassified" />
    ///     with zero confidence when the LLM declines to classify.
    /// </summary>
    Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                  string libraryHint,
                                                                  CancellationToken ct = default);
}
