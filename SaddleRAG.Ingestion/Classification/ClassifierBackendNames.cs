// ClassifierBackendNames.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Canonical backend identifier strings shared by every classifier
///     backend and its callers, so the identity contract has a single source
///     of truth (no per-file duplicated literals that can silently diverge).
/// </summary>
public static class ClassifierBackendNames
{
    /// <summary>
    ///     The local ONNX GenAI classifier backend.
    /// </summary>
    public const string Onnx = "onnx";

    /// <summary>
    ///     The optional Ollama classifier backend.
    /// </summary>
    public const string Ollama = "ollama";
}
