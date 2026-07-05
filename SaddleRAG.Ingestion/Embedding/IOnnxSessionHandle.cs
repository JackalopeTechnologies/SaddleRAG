// IOnnxSessionHandle.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.ML.OnnxRuntime;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Thin seam over <see cref="InferenceSession" /> so
///     <see cref="RecoverableOnnxSession" />'s rebuild policy is unit-testable
///     without ONNX model files (same pattern as the injectable append
///     delegates on <see cref="OnnxExecutionProviderConfigurator" /> —
///     <c>OnnxRuntimeException</c> has no public constructor).
/// </summary>
internal interface IOnnxSessionHandle : IDisposable
{
    IReadOnlyDictionary<string, NodeMetadata> InputMetadata { get; }

    IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs);
}
