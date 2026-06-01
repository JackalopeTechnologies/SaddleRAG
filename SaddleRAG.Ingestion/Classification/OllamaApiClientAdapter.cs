// OllamaApiClientAdapter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using OllamaSharp;
using OllamaSharp.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Ollama adapter that satisfies <see cref="IOllamaGenerateClient" />
///     by forwarding to the real <see cref="OllamaApiClient.GenerateAsync" />.
///     Registered as a singleton alongside <see cref="OllamaLlmClassifier" />
///     so the DI container supplies it.
/// </summary>
internal sealed class OllamaApiClientAdapter : IOllamaGenerateClient
{
    public OllamaApiClientAdapter(OllamaApiClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        mClient = client;
    }

    private readonly OllamaApiClient mClient;

    /// <inheritdoc />
    public IAsyncEnumerable<GenerateResponseStream?> GenerateAsync(GenerateRequest request,
                                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return mClient.GenerateAsync(request, ct);
    }
}
