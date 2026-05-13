// OnnxRuntimeCapabilitiesTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxRuntimeCapabilitiesTests
{
    [Fact]
    public void DefaultStateAlwaysIncludesCpuAndDefaultsActiveToCpu()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        Assert.Contains(OnnxSettings.ExecutionProviderCpu, capabilities.CompiledInProviders);
        Assert.Equal(OnnxSettings.ExecutionProviderCpu, capabilities.ActiveProvider);
        Assert.Null(capabilities.LastLoadWarning);
    }

    [Fact]
    public void CompiledInProvidersReflectsBuildFlavor()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        // CPU is always compiled in; DirectMl is only compiled in when the
        // USE_GPU symbol is defined. The capability list must match.
#if USE_GPU
        Assert.Contains(OnnxSettings.ExecutionProviderDirectMl, capabilities.CompiledInProviders);
#else
        Assert.DoesNotContain(OnnxSettings.ExecutionProviderDirectMl, capabilities.CompiledInProviders);
#endif
    }

    [Fact]
    public void RecordLoadOutcomeUpdatesActiveProviderAndWarning()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        capabilities.RecordLoadOutcome(requested: "DirectMl",
                                       actual: OnnxSettings.ExecutionProviderCpu,
                                       warning: "DirectML not compiled in"
                                      );

        Assert.Equal(OnnxSettings.ExecutionProviderCpu, capabilities.ActiveProvider);
        Assert.Equal("DirectML not compiled in", capabilities.LastLoadWarning);
    }

    [Fact]
    public void RecordLoadOutcomeClearsWarningOnSuccess()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        capabilities.RecordLoadOutcome(requested: "DirectMl",
                                       actual: OnnxSettings.ExecutionProviderCpu,
                                       warning: "fell back"
                                      );

        capabilities.RecordLoadOutcome(requested: OnnxSettings.ExecutionProviderCpu,
                                       actual: OnnxSettings.ExecutionProviderCpu,
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
            capabilities.RecordLoadOutcome(requested: OnnxSettings.ExecutionProviderCuda,
                                           actual: OnnxSettings.ExecutionProviderCuda,
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
            capabilities.RecordLoadOutcome(requested: OnnxSettings.ExecutionProviderDirectMl,
                                           actual: OnnxSettings.ExecutionProviderCpu,
                                           warning: null
                                          )
        );
        Assert.Contains("Silent fallbacks", ex.Message);
    }
}
