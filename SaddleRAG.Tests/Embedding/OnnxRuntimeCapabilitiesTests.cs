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

        // The reported provider must match the native package selected at build time.
#if USE_GPU_CUDA
        Assert.Contains(OnnxExecutionProvider.Cuda, capabilities.CompiledInProviders);
        Assert.DoesNotContain(OnnxExecutionProvider.DirectMl, capabilities.CompiledInProviders);
#elif USE_GPU
        Assert.Contains(OnnxExecutionProvider.DirectMl, capabilities.CompiledInProviders);
        Assert.DoesNotContain(OnnxExecutionProvider.Cuda, capabilities.CompiledInProviders);
#else
        Assert.DoesNotContain(OnnxExecutionProvider.DirectMl, capabilities.CompiledInProviders);
        Assert.DoesNotContain(OnnxExecutionProvider.Cuda, capabilities.CompiledInProviders);
#endif
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

        OnnxExecutionProvider unsupported = Enum.GetValues<OnnxExecutionProvider>()
            .First(provider => !capabilities.CompiledInProviders.Contains(provider));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            capabilities.RecordLoadOutcome(unsupported,
                                           unsupported,
                                           warning: null
                                          )
        );
        Assert.Contains("CompiledInProviders", ex.Message);
    }

    [Fact]
    public void RecordLoadOutcomeAcceptsEveryCompiledProvider()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        foreach (OnnxExecutionProvider provider in capabilities.CompiledInProviders)
        {
            capabilities.RecordLoadOutcome(provider, provider, warning: null);
            Assert.Equal(provider, capabilities.ActiveProvider);
            Assert.Equal(provider, capabilities.RequestedProvider);
            Assert.Null(capabilities.LastLoadWarning);
        }
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
