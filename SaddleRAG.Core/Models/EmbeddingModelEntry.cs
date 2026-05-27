// EmbeddingModelEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     One entry in the <c>Onnx.EmbeddingModels</c> registry. Describes a
///     specific ONNX embedding model the provider can load. The first entry
///     in the list is the default when <c>Onnx.ActiveEmbeddingModel</c> is
///     unset; otherwise the active entry is the one whose <see cref="Name" />
///     matches <c>ActiveEmbeddingModel</c>.
/// </summary>
public class EmbeddingModelEntry
{
    /// <summary>
    ///     Stable identifier. Used as the on-disk directory name under
    ///     <c>OnnxSettings.ModelsDir</c> and surfaced as
    ///     <c>IEmbeddingProvider.ModelName</c>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Human-readable rationale for why this model is offered. Visible at
    ///     config time so a user reading appsettings.json understands the
    ///     tradeoffs without leaving the file.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     HuggingFace repository identifier (e.g. <c>"nomic-ai/nomic-embed-text-v1.5"</c>).
    /// </summary>
    public string RepoId { get; set; } = string.Empty;

    /// <summary>
    ///     Path within the HuggingFace repo to the ONNX model file
    ///     (e.g. <c>"onnx/model_fp16.onnx"</c>).
    /// </summary>
    public string ModelFile { get; set; } = string.Empty;

    /// <summary>
    ///     Tokenizer architecture used by this model. Determines which
    ///     tokenizer code path the provider invokes.
    /// </summary>
    public TokenizerFamily TokenizerFamily { get; set; } = TokenizerFamily.Bert;

    /// <summary>
    ///     Path within the HF repo to the BERT vocab file (e.g.
    ///     <c>"vocab.txt"</c>). Used by <see cref="TokenizerFamily.Bert" />.
    ///     Empty for non-BERT families.
    /// </summary>
    public string VocabFile { get; set; } = string.Empty;

    /// <summary>
    ///     Path within the HF repo to the SentencePiece model file
    ///     (e.g. <c>"spm.model"</c>). Used by
    ///     <see cref="TokenizerFamily.SentencePiece" />. Empty otherwise.
    /// </summary>
    public string SpmFile { get; set; } = string.Empty;

    /// <summary>
    ///     Output vector dimension. Must match the model's actual output;
    ///     the provider validates this at load time.
    /// </summary>
    public int Dimensions { get; set; }

    /// <summary>
    ///     Token cap per input. Inputs longer than this are truncated.
    /// </summary>
    public int MaxSequenceLength { get; set; } = 512;

    /// <summary>
    ///     Optional task prefix prepended to documents (e.g. nomic's
    ///     <c>"search_document: "</c>). Empty for models that don't use
    ///     task prefixes.
    /// </summary>
    public string DocumentPrefix { get; set; } = string.Empty;

    /// <summary>
    ///     Optional task prefix prepended to queries (e.g. nomic's
    ///     <c>"search_query: "</c>). Empty for models that don't use
    ///     task prefixes.
    /// </summary>
    public string QueryPrefix { get; set; } = string.Empty;
}
