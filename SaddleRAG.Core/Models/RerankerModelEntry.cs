// RerankerModelEntry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Core.Models;

/// <summary>
///     One entry in the <c>Onnx.RerankerModels</c> registry. Describes a
///     specific ONNX cross-encoder reranker model. The first entry in the
///     list is the default when <c>Onnx.ActiveRerankerModel</c> is unset.
///     <c>ActiveRerankerModel</c> set to empty or null disables reranking
///     entirely regardless of registry contents.
/// </summary>
public class RerankerModelEntry
{
    /// <summary>
    ///     Stable identifier. Used as the on-disk directory name under
    ///     <c>OnnxSettings.ModelsDir</c> and surfaced as
    ///     <c>IReRanker.ModelName</c>.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Human-readable rationale for why this model is offered.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    ///     HuggingFace repository identifier.
    /// </summary>
    public string RepoId { get; set; } = string.Empty;

    /// <summary>
    ///     Path within the HuggingFace repo to the ONNX model file.
    /// </summary>
    public string ModelFile { get; set; } = string.Empty;

    /// <summary>
    ///     Tokenizer architecture used by this reranker.
    /// </summary>
    public TokenizerFamily TokenizerFamily { get; set; } = TokenizerFamily.SentencePiece;

    /// <summary>
    ///     Path within the HF repo to the BERT vocab file. Used by
    ///     <see cref="TokenizerFamily.Bert" />.
    /// </summary>
    public string VocabFile { get; set; } = string.Empty;

    /// <summary>
    ///     Path within the HF repo to the SentencePiece model file
    ///     (e.g. <c>"spm.model"</c>).
    /// </summary>
    public string SpmFile { get; set; } = string.Empty;

    /// <summary>
    ///     Max combined <c>[CLS] query [SEP] doc [SEP]</c> length per pair.
    ///     Pairs longer than this are truncated document-side first.
    /// </summary>
    public int MaxSequenceLength { get; set; } = 512;

    /// <summary>
    ///     Mapping of special token strings to their IDs (e.g.
    ///     <c>{ "[CLS]": 1, "[SEP]": 2 }</c>). Required for
    ///     <see cref="TokenizerFamily.SentencePiece" /> which doesn't
    ///     auto-add specials. Empty for <see cref="TokenizerFamily.Bert" />.
    /// </summary>
    public Dictionary<string, int> SpecialTokens { get; set; } = new();
}
