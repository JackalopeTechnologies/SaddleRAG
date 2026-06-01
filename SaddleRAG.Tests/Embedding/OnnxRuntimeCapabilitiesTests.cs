// OnnxRuntimeCapabilitiesTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxRuntimeCapabilitiesTests
{
    [Fact]
    public void DefaultStateAlwaysIncludesCpuAndDefaultsActiveToCpu()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        Assert.Contains(OnnxExecutionProvider.Cpu, capabilities.CompiledInProviders);
        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.ActiveProvider);
        Assert.Null(capabilities.LastLoadWarning);
    }

    [Fact]
    public void CompiledInProvidersReflectsBuildFlavor()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        // CPU is always compiled in; DirectMl is only compiled in when the
        // USE_GPU symbol is defined. The capability list must match.
        // Cuda must never appear today regardless of flavor — the GPU NuGet
        // is the DirectML one, not Microsoft.ML.OnnxRuntime.Gpu. Asserting
        // its absence locks the IsSupportedByBuild contract documented on
        // OnnxExecutionProvider.Cuda; relax this assertion only when a
        // Cuda-flavored build flavor is actually added.
#if USE_GPU
        Assert.Contains(OnnxExecutionProvider.DirectMl, capabilities.CompiledInProviders);
#else
        Assert.DoesNotContain(OnnxExecutionProvider.DirectMl, capabilities.CompiledInProviders);
#endif
        Assert.DoesNotContain(OnnxExecutionProvider.Cuda, capabilities.CompiledInProviders);
    }

    [Fact]
    public void RecordLoadOutcomeUpdatesActiveProviderAndWarning()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        capabilities.RecordLoadOutcome(OnnxExecutionProvider.DirectMl,
                                       OnnxExecutionProvider.Cpu,
                                       "DirectML not compiled in"
                                      );

        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.ActiveProvider);
        Assert.Equal("DirectML not compiled in", capabilities.LastLoadWarning);
    }

    [Fact]
    public void RecordLoadOutcomeClearsWarningOnSuccess()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        capabilities.RecordLoadOutcome(OnnxExecutionProvider.DirectMl,
                                       OnnxExecutionProvider.Cpu,
                                       "fell back"
                                      );

        capabilities.RecordLoadOutcome(OnnxExecutionProvider.Cpu,
                                       OnnxExecutionProvider.Cpu,
                                       warning: null
                                      );

        Assert.Null(capabilities.LastLoadWarning);
    }

    [Fact]
    public void RecordLoadOutcomeRejectsActualNotInCompiledInProviders()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        // CPU-only build can't legitimately have recorded actual=Cuda;
        // GPU build doesn't compile in Cuda either (only DirectMl). Either
        // way recording Cuda as the actual loaded EP is a bug.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            capabilities.RecordLoadOutcome(OnnxExecutionProvider.Cuda,
                                           OnnxExecutionProvider.Cuda,
                                           warning: null
                                          )
        );
        Assert.Contains("CompiledInProviders", ex.Message);
    }

    [Fact]
    public void RecordLoadOutcomeRejectsSilentFallback()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            capabilities.RecordLoadOutcome(OnnxExecutionProvider.DirectMl,
                                           OnnxExecutionProvider.Cpu,
                                           warning: null
                                          )
        );
        Assert.Contains("Silent fallbacks", ex.Message);
    }
}
