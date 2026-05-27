// TokenizerFamily.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Enums;

/// <summary>
///     Tokenizer architecture used by an ONNX model entry in the registry.
///     Adding a new family requires C# tokenizer code; adding a new model
///     within an existing family is a config-only change.
/// </summary>
public enum TokenizerFamily
{
    /// <summary>
    ///     BERT WordPiece (e.g. nomic-embed-text-v1.5,
    ///     cross-encoder/ms-marco-MiniLM-L6-v2). Tokenizer is built from
    ///     vocab.txt via <c>Microsoft.ML.Tokenizers.BertTokenizer.Create</c>.
    ///     Adds [CLS]/[SEP] automatically; supports cross-encoder pair framing
    ///     via BuildInputsWithSpecialTokens / CreateTokenTypeIdsFromSequences.
    /// </summary>
    Bert,

    /// <summary>
    ///     SentencePiece (e.g. mxbai-rerank-base-v1, mxbai-rerank-large-v1,
    ///     which are DeBERTa-v2 family). Tokenizer is built from a binary
    ///     spm.model file via <c>Microsoft.ML.Tokenizers.SentencePieceTokenizer.Create</c>.
    ///     Does NOT auto-add special tokens; the provider must manually frame
    ///     [CLS] query [SEP] doc [SEP] using SpecialTokens from the entry.
    /// </summary>
    SentencePiece
}
