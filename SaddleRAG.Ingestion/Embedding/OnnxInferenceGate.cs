// OnnxInferenceGate.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Process-wide gate that serializes every
///     <c>Microsoft.ML.OnnxRuntime.InferenceSession.Run()</c> call
///     across every ONNX session in the process (embedding model,
///     reranker model, anything else added later).
///     <para>
///         DirectML cannot tolerate concurrent <c>Run()</c> calls
///         against the same GPU device, even across different
///         <c>InferenceSession</c> instances. Two sessions racing on
///         a single DirectML device manifests at runtime as either a
///         Windows Timeout Detection and Recovery (TDR) crash
///         (<c>887A0005 The GPU device instance has been suspended</c>)
///         or an unrecoverable native crash that surfaces in managed
///         code as <see cref="System.ExecutionEngineException" /> out of
///         <c>Microsoft.ML.OnnxRuntime.dll</c>. Both kill the host
///         process.
///     </para>
///     <para>
///         The earlier per-class <c>SemaphoreSlim</c> in
///         <see cref="OnnxEmbeddingProvider" /> only serialized the
///         embedding session against itself. The reranker session
///         shared the GPU and ran kernels concurrently with the
///         embedder, which was the trigger for the
///         <see cref="System.ExecutionEngineException" /> the second
///         time around. Hoisting the gate to process scope is the
///         correct level — there is one GPU device per process, so
///         there should be one inference gate per process.
///     </para>
///     <para>
///         Throughput cost is essentially zero: the GPU is already
///         the serial bottleneck. The gate just makes the existing
///         serialization explicit and crash-proof. Callers should
///         <see cref="Acquire" /> immediately before <c>Run()</c> and
///         <see cref="Release" /> in a <c>finally</c> block after the
///         result is fully consumed (the tensor view from
///         <c>Run()</c> dies with the result-disposable, so the gate
///         must stay held until pooling/normalization completes).
///     </para>
/// </summary>
internal static class OnnxInferenceGate
{
    /// <summary>
    ///     Blocks the current thread until the gate is free, then
    ///     marks it held. Always pair with <see cref="Release" /> in
    ///     a <c>finally</c> block.
    /// </summary>
    public static void Acquire() => smGate.Wait();

    /// <summary>
    ///     Releases the gate so the next waiting caller can run.
    /// </summary>
    public static void Release() => smGate.Release();

    private static readonly SemaphoreSlim smGate = new SemaphoreSlim(initialCount: 1, maxCount: 1);
}
