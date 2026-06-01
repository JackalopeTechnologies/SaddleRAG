// MonitorConfigOllama.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Ollama card on the Monitor /config page (issue #73). All four
///     fields are surfaced as-is — no masking needed since Ollama
///     endpoints in SaddleRAG deployments are typically local HTTP and
///     model names are not secret.
/// </summary>
public sealed record MonitorConfigOllama(
    string Endpoint,
    string ClassificationModel,
    string ReconModel,
    string EmbeddingModel);
