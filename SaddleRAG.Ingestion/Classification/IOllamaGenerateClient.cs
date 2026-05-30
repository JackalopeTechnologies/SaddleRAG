// IOllamaGenerateClient.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using OllamaSharp.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Minimal seam over the OllamaSharp generate API used by
///     <see cref="OllamaLlmClassifier" />. Abstracts the single
///     streaming-generate call so the classifier's prompt-building, output
///     parsing, and failure handling can be unit-tested with a fake without
///     requiring a live Ollama endpoint.
///     The real implementation is <see cref="OllamaApiClientAdapter" />;
///     tests supply a fake that returns canned token streams.
/// </summary>
internal interface IOllamaGenerateClient
{
    /// <summary>
    ///     Streams token chunks for <paramref name="request" />.
    ///     Each element carries a partial <see cref="GenerateResponseStream.Response" />
    ///     string; callers concatenate them to form the full output.
    /// </summary>
    IAsyncEnumerable<GenerateResponseStream?> GenerateAsync(GenerateRequest request,
                                                            CancellationToken ct = default);
}
