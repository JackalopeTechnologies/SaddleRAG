// TokenizerFamily.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
    SentencePiece,

    /// <summary>
    ///     XLM-Roberta (e.g. jinaai/jina-reranker-v2-base-multilingual). The
    ///     SentencePiece model is embedded inside tokenizer.json rather than
    ///     shipped as a standalone spm file. Not yet implemented — selecting
    ///     this family at runtime throws <see cref="System.NotImplementedException" />.
    ///     Tracked as future work: extract the SP model from tokenizer.json
    ///     once at install time, or write a custom XLM-Roberta wrapper.
    /// </summary>
    XlmRoberta
}
