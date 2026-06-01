// MonitorConfigEmbedding.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Embedding-provider card on the Monitor /config page (issue #73).
///     <see cref="ProviderId" />, <see cref="ModelName" />, and
///     <see cref="Dimensions" /> come from the live <c>IEmbeddingProvider</c>;
///     <see cref="OnnxBacked" /> reflects the OnnxSettings.Enabled +
///     EmbeddingEnabled combination that the DI registration uses to pick
///     OnnxEmbeddingProvider vs. OllamaEmbeddingProvider.
/// </summary>
public sealed record MonitorConfigEmbedding(
    string ProviderId,
    string ModelName,
    int Dimensions,
    bool OnnxBacked,
    bool OnnxEmbeddingEnabled);
