// OnnxExecutionProviderConfiguratorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxExecutionProviderConfiguratorTests
{
    [Fact]
    public void ConfigureWithCpuRecordsCpuAndNoWarning()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();

        OnnxExecutionProviderConfigurator.Configure(options, OnnxExecutionProvider.Cpu,
                                                    capabilities, NullLogger.Instance
                                                   );

        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.ActiveProvider);
        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.RequestedProvider);
        Assert.Null(capabilities.LastLoadWarning);
    }

    [Fact]
    public void ConfigureWithDirectMlOnCpuOnlyBuildFallsBackToCpuAndWarns()
    {
#if USE_GPU
        Assert.Skip("DirectML compiled in; this test only meaningful on CPU-only builds.");
#else
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();

        OnnxExecutionProviderConfigurator.Configure(options, OnnxExecutionProvider.DirectMl,
                                                    capabilities, NullLogger.Instance
                                                   );

        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.ActiveProvider);
        Assert.Equal(OnnxExecutionProvider.DirectMl, capabilities.RequestedProvider);
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

        OnnxExecutionProviderConfigurator.Configure(options, OnnxExecutionProvider.Cuda,
                                                    capabilities, NullLogger.Instance
                                                   );

        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.ActiveProvider);
        Assert.Equal(OnnxExecutionProvider.Cuda, capabilities.RequestedProvider);
        Assert.NotNull(capabilities.LastLoadWarning);
#endif
    }
}
