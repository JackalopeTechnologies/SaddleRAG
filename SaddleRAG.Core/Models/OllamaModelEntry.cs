// OllamaModelEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     One entry in an Ollama model registry. Used by the
///     <c>ClassificationModels</c> and <c>ReconModels</c> lists in
///     <c>OllamaSettings</c>. First entry in the list is the active model
///     when the corresponding <c>Active{...}Model</c> selector is empty.
/// </summary>
public class OllamaModelEntry
{
    /// <summary>
    ///     Ollama model tag (e.g. <c>"phi4-mini:3.8b"</c>). Passed directly
    ///     to the Ollama API on pull and inference calls.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Human-readable rationale for why this model is offered for this
    ///     task — VRAM footprint, instruction-following characteristics,
    ///     supply-chain notes, etc. Visible at config time.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
