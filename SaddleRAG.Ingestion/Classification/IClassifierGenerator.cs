// IClassifierGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Seam over the local GenAI text-generation backend used by
///     <see cref="OnnxLlmClassifier" />. Abstracts every
///     <c>Microsoft.ML.OnnxRuntimeGenAI</c> call behind a single method so the
///     classifier's prompt-building, output parsing, and failure handling can
///     be unit-tested with a fake — exercising a real model requires a
///     multi-gigabyte download and native runtime, which is out of scope for
///     unit tests.
///     The generation parameters (max output tokens, temperature, stop token)
///     come from the active <see cref="SaddleRAG.Core.Models.ClassifierModelEntry" />
///     and are applied inside the concrete implementation
///     (<see cref="OnnxClassifierGenerator" />), so this interface stays minimal
///     and the classifier never references the native types.
/// </summary>
public interface IClassifierGenerator
{
    /// <summary>
    ///     Generates the model's completion for <paramref name="prompt" /> and
    ///     returns the raw decoded text (no parsing). The implementation is
    ///     responsible for applying the model's generation parameters and for
    ///     halting at the configured stop token.
    /// </summary>
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
}
