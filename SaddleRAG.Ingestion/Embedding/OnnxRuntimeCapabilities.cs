// OnnxRuntimeCapabilities.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Singleton state-bag that records which ONNX Runtime execution
///     providers (EPs) are compiled into the current build flavor and
///     which EP the running embedding / reranker sessions ended up
///     loading with after the EP-append attempt. The
///     <c>list_execution_providers</c> MCP tool reads this so the LLM
///     can see whether a requested GPU provider actually took effect or
///     silently fell back to CPU.
///     "Compiled in" means the C# bindings + native ORT binaries for
///     that EP are present. CPU is always compiled in. DirectML and CUDA
///     are gated on the <c>USE_GPU</c> conditional-compilation symbol,
///     which the <c>UseGpu</c> MSBuild property in
///     <c>SaddleRAG.Ingestion.csproj</c> defines when the GPU NuGet is
///     referenced. A provider being compiled in is necessary but not
///     sufficient for it to load — the underlying hardware / drivers
///     have to support it too; the actual outcome is captured in
///     <see cref="ActiveProvider" /> and <see cref="LastLoadWarning" />
///     by <see cref="OnnxExecutionProviderConfigurator" />.
/// </summary>
public class OnnxRuntimeCapabilities
{
    public OnnxRuntimeCapabilities()
    {
        var compiled = new List<string> { OnnxSettings.ExecutionProviderCpu };
#if USE_GPU
        compiled.Add(OnnxSettings.ExecutionProviderDirectMl);
#endif
        mCompiledInProviders = compiled;
    }

    private readonly List<string> mCompiledInProviders;

    /// <summary>
    ///     EPs the running build is capable of attempting. CPU is always
    ///     present. DirectML is present when <c>USE_GPU</c> is defined.
    /// </summary>
    public IReadOnlyList<string> CompiledInProviders => mCompiledInProviders;

    /// <summary>
    ///     The EP the most-recently-constructed embedding or reranker
    ///     session is actually using. Starts as <c>"Cpu"</c> and is
    ///     updated by <see cref="RecordLoadOutcome" />.
    /// </summary>
    public string ActiveProvider { get; private set; } = OnnxSettings.ExecutionProviderCpu;

    /// <summary>
    ///     The EP the most-recent session was configured to attempt
    ///     (from <c>OnnxSettings.ExecutionProvider</c>). Distinct from
    ///     <see cref="ActiveProvider" /> when a GPU EP was requested but
    ///     the session fell back to CPU.
    /// </summary>
    public string RequestedProvider { get; private set; } = OnnxSettings.ExecutionProviderCpu;

    /// <summary>
    ///     Free-form warning text from the most-recent EP-append attempt,
    ///     or null if the attempt succeeded. Surfaces typos and missing
    ///     hardware to the LLM via <c>list_execution_providers</c>.
    /// </summary>
    public string? LastLoadWarning { get; private set; }

    /// <summary>
    ///     Records the outcome of an EP-append attempt. Called by
    ///     <see cref="OnnxExecutionProviderConfigurator" /> after each
    ///     attempted session-options configuration.
    /// </summary>
    public void RecordLoadOutcome(string requested, string actual, string? warning)
    {
        ArgumentException.ThrowIfNullOrEmpty(requested);
        ArgumentException.ThrowIfNullOrEmpty(actual);

        RequestedProvider = requested;
        ActiveProvider = actual;
        LastLoadWarning = warning;
    }
}
