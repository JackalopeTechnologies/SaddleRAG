// ClassifierModelEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>
///     One entry in the <c>Onnx.ClassifierModels</c> registry. Describes a
///     specific GenAI ONNX classifier model the provider can load. The first
///     entry in the list is the default when <c>Onnx.ActiveClassifierModel</c>
///     is unset.
///     GenAI models ship as a <em>folder</em> of files rather than a single
///     <c>.onnx</c> file; each provider variant (CPU, DirectML, CUDA) is a
///     distinct subfolder within the same HuggingFace repo. Represent each
///     variant as a separate registry entry so the active entry selection
///     mechanism — set <see cref="OnnxSettings.ActiveClassifierModel" /> to the
///     desired name — can also select the provider-appropriate folder without
///     changing any other config.
/// </summary>
public class ClassifierModelEntry
{
    /// <summary>
    ///     Stable identifier. Used as the on-disk directory name under
    ///     <c>OnnxSettings.ModelsDir</c> and surfaced as the classifier
    ///     model name in MCP tool responses.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Human-readable rationale for why this entry is offered (e.g.
    ///     which provider variant it targets). Visible at config time so
    ///     a user reading appsettings.json understands the tradeoffs
    ///     without leaving the file.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     HuggingFace repository identifier (e.g.
    ///     <c>"microsoft/Phi-4-mini-instruct-onnx"</c>).
    /// </summary>
    public string RepoId { get; set; } = string.Empty;

    /// <summary>
    ///     Provider-specific subfolder within the HuggingFace repo that
    ///     contains the complete GenAI model (e.g.
    ///     <c>"cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4"</c> for
    ///     the CPU variant). GenAI models are a folder of files, not a
    ///     single <c>.onnx</c> file, so this identifies the root of
    ///     that folder inside the repo.
    /// </summary>
    public string ModelFolder { get; set; } = string.Empty;

    /// <summary>
    ///     Maximum token length the model supports for its combined prompt
    ///     and output. Inputs that exceed this length must be truncated
    ///     before inference.
    /// </summary>
    public int MaxContextLength { get; set; } = DefaultMaxContextLength;

    /// <summary>
    ///     Maximum number of new tokens the model may generate per
    ///     inference call. Kept small for classification tasks where
    ///     the expected output is a short JSON fragment.
    /// </summary>
    public int MaxOutputTokens { get; set; } = DefaultMaxOutputTokens;

    /// <summary>
    ///     Sampling temperature. 0.0 produces deterministic (greedy)
    ///     output, which is appropriate for classification tasks that
    ///     expect a specific structured response.
    /// </summary>
    public float Temperature { get; set; } = DefaultTemperature;

    /// <summary>
    ///     Stop token appended by the model to signal end of a structured
    ///     response. Generation halts when this token is produced.
    ///     Default is <c>"&lt;/json&gt;"</c>, which pairs with a prompt
    ///     that asks the model to wrap its answer in
    ///     <c>&lt;json&gt;...&lt;/json&gt;</c> tags.
    /// </summary>
    public string Stop { get; set; } = DefaultStop;

    /// <summary>Default value for <see cref="MaxContextLength" />.</summary>
    public const int DefaultMaxContextLength = 4096;

    /// <summary>Default value for <see cref="MaxOutputTokens" />.</summary>
    public const int DefaultMaxOutputTokens = 256;

    /// <summary>Default value for <see cref="Temperature" />.</summary>
    public const float DefaultTemperature = 0.0f;

    /// <summary>Default value for <see cref="Stop" />.</summary>
    public const string DefaultStop = "</json>";
}
