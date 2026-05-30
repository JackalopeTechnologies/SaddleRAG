// IOllamaProbe.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Seam over <see cref="OllamaProbe" /> used by
///     <see cref="ClassifierBackendSwitch" /> to check Ollama reachability
///     before switching to the Ollama backend. Extracted so the switch can
///     be unit-tested without standing up a real HTTP server.
/// </summary>
public interface IOllamaProbe
{
    /// <summary>
    ///     Returns <see langword="true" /> when Ollama is reachable and
    ///     answering requests; <see langword="false" /> otherwise.
    /// </summary>
    Task<bool> IsReachableAsync(CancellationToken ct = default);
}
