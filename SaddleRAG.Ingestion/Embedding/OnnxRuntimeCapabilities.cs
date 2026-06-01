// OnnxRuntimeCapabilities.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

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
        var compiled = new List<OnnxExecutionProvider> { OnnxExecutionProvider.Cpu };
#if USE_GPU
        compiled.Add(OnnxExecutionProvider.DirectMl);
#endif
        mCompiledInProviders = compiled;
    }

    private readonly List<OnnxExecutionProvider> mCompiledInProviders;

    /// <summary>
    ///     EPs the running build is capable of attempting. CPU is always
    ///     present. DirectML is present when <c>USE_GPU</c> is defined.
    /// </summary>
    public IReadOnlyList<OnnxExecutionProvider> CompiledInProviders => mCompiledInProviders;

    /// <summary>
    ///     The EP the most-recently-constructed embedding or reranker
    ///     session is actually using. Starts as <see cref="OnnxExecutionProvider.Cpu" />
    ///     and is updated by <see cref="RecordLoadOutcome" />.
    /// </summary>
    public OnnxExecutionProvider ActiveProvider { get; private set; } = OnnxExecutionProvider.Cpu;

    /// <summary>
    ///     The EP the most-recent session was configured to attempt (from
    ///     <c>OnnxSettings.ExecutionProvider</c>). Distinct from
    ///     <see cref="ActiveProvider" /> when a GPU EP was requested but
    ///     the session fell back to CPU.
    /// </summary>
    public OnnxExecutionProvider RequestedProvider { get; private set; } = OnnxExecutionProvider.Cpu;

    /// <summary>
    ///     Free-form warning text from the most-recent EP-append attempt,
    ///     or null if the attempt succeeded. Surfaces typos and missing
    ///     hardware to the LLM via <c>list_execution_providers</c>.
    /// </summary>
    public string? LastLoadWarning { get; private set; }

    /// <summary>
    ///     Records the outcome of an EP-append attempt. Called by
    ///     <see cref="OnnxExecutionProviderConfigurator" /> after each
    ///     attempted session-options configuration. Enforces two invariants:
    ///     <list type="bullet">
    ///         <item>
    ///             <paramref name="actual" /> must be one of the EPs this
    ///             build is capable of loading (CPU is always allowed, GPU
    ///             providers are allowed only when <c>USE_GPU</c> is
    ///             defined — see <see cref="CompiledInProviders" />). A
    ///             caller recording <c>actual="Cuda"</c> on a CPU-only
    ///             build would corrupt diagnostic state.
    ///         </item>
    ///         <item>
    ///             If <paramref name="actual" /> differs from
    ///             <paramref name="requested" /> (i.e., a fallback
    ///             occurred), <paramref name="warning" /> must be
    ///             populated. Falling back silently is a bug.
    ///         </item>
    ///     </list>
    /// </summary>
    public void RecordLoadOutcome(OnnxExecutionProvider requested,
                                  OnnxExecutionProvider actual,
                                  string? warning)
    {
        if (!mCompiledInProviders.Contains(actual))
            throw new InvalidOperationException(
                string.Format(ActualNotCompiledInFormat, actual, string.Join(",", mCompiledInProviders))
            );

        bool fellBack = requested != actual;
        if (fellBack && string.IsNullOrEmpty(warning))
            throw new InvalidOperationException(string.Format(SilentFallbackFormat, requested, actual));

        RequestedProvider = requested;
        ActiveProvider = actual;
        LastLoadWarning = warning;
    }

    private const string ActualNotCompiledInFormat = "OnnxRuntimeCapabilities.RecordLoadOutcome: actual='{0}' is not in CompiledInProviders [{1}]. The configurator must only record EPs the build actually supports.";

    private const string SilentFallbackFormat = "OnnxRuntimeCapabilities.RecordLoadOutcome: fallback from requested='{0}' to actual='{1}' without an explanatory warning. Silent fallbacks are a bug.";
}
