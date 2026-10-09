// OllamaKeepAlive.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     The keep-alive SaddleRAG sends on its own Ollama requests so the model it
///     uses stays loaded between requests. Asking per request keeps only
///     SaddleRAG's models resident; the machine-wide OLLAMA_KEEP_ALIVE the installer
///     used to set pinned every program's models in GPU memory instead.
/// </summary>
internal static class OllamaKeepAlive
{
    /// <summary>
    ///     "Keep loaded until unloaded." Ollama parses a string keep-alive as a
    ///     duration and rejects the bare "-1" ("missing unit in duration"); any
    ///     negative duration means keep loaded.
    /// </summary>
    public const string KeepLoaded = "-1m";
}
