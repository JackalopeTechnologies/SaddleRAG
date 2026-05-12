// OnnxExecutionProviderConfiguratorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxExecutionProviderConfiguratorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Cpu")]
    [InlineData("cpu")]
    public void ConfigureWithCpuSentinelRecordsCpuAndNoWarning(string requested)
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();

        OnnxExecutionProviderConfigurator.Configure(options, requested, capabilities,
                                                    NullLogger.Instance
                                                   );

        Assert.Equal(OnnxSettings.ExecutionProviderCpu, capabilities.ActiveProvider);
        Assert.Equal(OnnxSettings.ExecutionProviderCpu, capabilities.RequestedProvider);
        Assert.Null(capabilities.LastLoadWarning);
    }

    [Fact]
    public void ConfigureWithUnknownValueFallsBackToCpuAndWarns()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();

        OnnxExecutionProviderConfigurator.Configure(options, requested: "Vulkan",
                                                    capabilities, NullLogger.Instance
                                                   );

        Assert.Equal(OnnxSettings.ExecutionProviderCpu, capabilities.ActiveProvider);
        Assert.Equal("Vulkan", capabilities.RequestedProvider);
        Assert.NotNull(capabilities.LastLoadWarning);
        Assert.Contains("Vulkan", capabilities.LastLoadWarning);
    }

    [Fact]
    public void ConfigureWithDirectMlOnCpuOnlyBuildFallsBackToCpuAndWarns()
    {
#if USE_GPU
        Assert.Skip("DirectML compiled in; this test only meaningful on CPU-only builds.");
#else
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();

        OnnxExecutionProviderConfigurator.Configure(options,
                                                    OnnxSettings.ExecutionProviderDirectMl,
                                                    capabilities, NullLogger.Instance
                                                   );

        Assert.Equal(OnnxSettings.ExecutionProviderCpu, capabilities.ActiveProvider);
        Assert.Equal(OnnxSettings.ExecutionProviderDirectMl, capabilities.RequestedProvider);
        Assert.NotNull(capabilities.LastLoadWarning);
        Assert.Contains("CPU-only", capabilities.LastLoadWarning);
#endif
    }

    [Fact]
    public void ConfigureWithCudaOnCpuOnlyBuildFallsBackToCpuAndWarns()
    {
#if USE_GPU
        Assert.Skip("GPU build; CUDA may or may not be compiled in depending on package choice — this test targets the CPU-only contract.");
#else
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();

        OnnxExecutionProviderConfigurator.Configure(options,
                                                    OnnxSettings.ExecutionProviderCuda,
                                                    capabilities, NullLogger.Instance
                                                   );

        Assert.Equal(OnnxSettings.ExecutionProviderCpu, capabilities.ActiveProvider);
        Assert.Equal(OnnxSettings.ExecutionProviderCuda, capabilities.RequestedProvider);
        Assert.NotNull(capabilities.LastLoadWarning);
#endif
    }
}
