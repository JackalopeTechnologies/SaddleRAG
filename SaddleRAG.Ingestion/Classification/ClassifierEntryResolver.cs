// ClassifierEntryResolver.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Pure resolver that picks the active <see cref="ClassifierModelEntry" />
///     from the <c>Onnx.ClassifierModels</c> registry to match the runtime
///     ONNX execution provider.
///     Resolution order:
///     <list type="number">
///         <item>
///             If <see cref="OnnxSettings.ActiveClassifierModel" /> is set
///             (non-empty) it is an explicit user override and is honored via
///             <see cref="OnnxSettings.GetActiveClassifierModel" /> regardless
///             of the runtime EP.
///         </item>
///         <item>
///             Otherwise the EP selects the variant: DirectMl &#8594; the
///             <c>*-directml</c> entry, Cuda &#8594; the <c>*-cuda</c> entry,
///             Cpu &#8594; the <c>*-cpu</c> entry, matched by registry
///             <see cref="ClassifierModelEntry.Name" />.
///         </item>
///         <item>
///             If the EP-matching entry isn't found, fall back to the CPU
///             entry; if that is also absent, fall back to the first entry.
///         </item>
///     </list>
///     GenAI classifier models ship as provider-specific folder trees, so the
///     variant must match the EP the embedding/reranker sessions loaded with —
///     loading a DirectML-quantised model under the CPU EP (or vice versa) is
///     not portable.
/// </summary>
public static class ClassifierEntryResolver
{
    /// <summary>
    ///     Resolves the active classifier entry for <paramref name="runtimeProvider" />.
    ///     Honors an explicit <see cref="OnnxSettings.ActiveClassifierModel" />
    ///     override first, otherwise auto-resolves by EP with a CPU fallback.
    /// </summary>
    /// <param name="settings">The bound ONNX settings (registry + active selector).</param>
    /// <param name="runtimeProvider">
    ///     The execution provider the runtime actually loaded with (from
    ///     <see cref="OnnxRuntimeCapabilities" /> / the configured
    ///     <see cref="OnnxSettings.ExecutionProvider" />).
    /// </param>
    /// <returns>The classifier entry to construct the generator from.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The registry is empty, or an explicit override names an entry that
    ///     does not exist.
    /// </exception>
    /// <summary>
    ///     Resolves the active classifier entry for <paramref name="requestedProvider" />,
    ///     first clamping it to <paramref name="compiledInProviders" /> (falling back to
    ///     <see cref="OnnxExecutionProvider.Cpu" /> when the requested EP is not compiled
    ///     into this build). Guards against runtime-overrides requesting a GPU EP on a
    ///     CPU-only build: resolving the DirectML model variant against the CPU GenAI
    ///     native access-violates in <c>OgaCreateGenerator</c> and kills the process
    ///     (issue #135). An explicit <see cref="OnnxSettings.ActiveClassifierModel" />
    ///     override still wins unconditionally, matching the single-parameter overload.
    /// </summary>
    /// <param name="settings">The bound ONNX settings (registry + active selector).</param>
    /// <param name="requestedProvider">The configured execution provider (appsettings or runtime overrides).</param>
    /// <param name="compiledInProviders">
    ///     The EPs this build flavor actually ships, from
    ///     <see cref="OnnxRuntimeCapabilities.CompiledInProviders" />.
    /// </param>
    /// <returns>The classifier entry to construct the generator from.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The registry is empty, or an explicit override names an entry that
    ///     does not exist.
    /// </exception>
    public static ClassifierModelEntry Resolve(OnnxSettings settings,
                                               OnnxExecutionProvider requestedProvider,
                                               IReadOnlyList<OnnxExecutionProvider> compiledInProviders)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(compiledInProviders);

        OnnxExecutionProvider effective = compiledInProviders.Contains(requestedProvider)
                                              ? requestedProvider
                                              : OnnxExecutionProvider.Cpu;

        return Resolve(settings, effective);
    }

    public static ClassifierModelEntry Resolve(OnnxSettings settings, OnnxExecutionProvider runtimeProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.ClassifierModels.Count == 0)
            throw new InvalidOperationException(EmptyRegistryMessage);

        ClassifierModelEntry result;
        if (!string.IsNullOrEmpty(settings.ActiveClassifierModel))
            result = settings.GetActiveClassifierModel();
        else
            result = ResolveByExecutionProvider(settings, runtimeProvider);

        return result;
    }

    private static ClassifierModelEntry ResolveByExecutionProvider(OnnxSettings settings,
                                                                   OnnxExecutionProvider runtimeProvider)
    {
        string preferredName = runtimeProvider switch
        {
            OnnxExecutionProvider.DirectMl => OnnxSettings.Phi3MiniDirectMlName,
            OnnxExecutionProvider.Cuda => OnnxSettings.Phi3MiniCudaName,
            var _ => OnnxSettings.Phi3MiniCpuName
        };

        ClassifierModelEntry result = FindByName(settings, preferredName)
                                      ?? FindByName(settings, OnnxSettings.Phi3MiniCpuName)
                                      ?? settings.ClassifierModels[index: 0];

        return result;
    }

    private static ClassifierModelEntry? FindByName(OnnxSettings settings, string name) =>
        settings.ClassifierModels.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));

    private const string EmptyRegistryMessage =
        "Onnx.ClassifierModels registry is empty; cannot resolve an active classifier model.";
}
