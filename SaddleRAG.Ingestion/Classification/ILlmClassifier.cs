// ILlmClassifier.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Minimal seam for the streaming-pipeline classify stage: single
///     per-page classification call. The concrete <see cref="LlmClassifier" />
///     also exposes <c>GetCurrentVersion</c>; that stays on the concrete type
///     because only <see cref="Recon.RescrubService" /> needs it.
/// </summary>
internal interface ILlmClassifier
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
