// IEmbeddingService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#pragma warning disable STR0010 // Interface methods cannot validate parameters


#region Usings

using SaddleRAG.Core.Enums;

#endregion


namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Generates embedding vectors from text content.
///     Multiple providers supported: Ollama (local), Azure OpenAI, OpenAI.
/// </summary>
public interface IEmbeddingProvider

{
    /// <summary>
    ///     Provider identifier for configuration and logging.
    ///     Example: "ollama"
    /// </summary>

    string ProviderId { get; }


    /// <summary>
    ///     Specific model name. Example: "nomic-embed-text"
    /// </summary>

    string ModelName { get; }


    /// <summary>
    ///     Dimensionality of the embedding vectors produced.
    /// </summary>

    int Dimensions { get; }


    /// <summary>
    ///     Generate embeddings for one or more texts. The
    ///     <paramref name="role" /> argument tells asymmetric models
    ///     (e.g. nomic-embed-text-v1.5) whether the text is being
    ///     embedded as a document for indexing or as a query for
    ///     retrieval — the model emits different vectors for each.
    ///     Symmetric models and Ollama-backed providers ignore the role.
    ///     Implementations should batch efficiently.
    /// </summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts,
                               EmbedRole role = EmbedRole.Document,
                               CancellationToken ct = default);
}
