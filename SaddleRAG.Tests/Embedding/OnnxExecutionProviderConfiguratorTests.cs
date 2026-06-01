// OnnxExecutionProviderConfiguratorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

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

    // The four tests below exercise the internal Configure overload with
    // injected EP-append delegates. This reaches the runtime-fallback
    // branch (OnnxRuntimeException caught, fall back to CPU, log error)
    // that the public overload's #if USE_GPU compile-time gate hides on
    // CPU-only builds. Without these tests, the catch was effectively
    // dead test surface on the default build flavor.

    [Fact]
    public void ConfigureWithInjectedDmlAppenderThatThrowsFallsBackToCpuAndWarns()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();
        Action<SessionOptions, int> failingAppender = (_, _) =>
            throw new SimulatedEpUnavailableException("no DX12 device");

        OnnxExecutionProviderConfigurator.Configure(options, OnnxExecutionProvider.DirectMl,
                                                    capabilities, NullLogger.Instance,
                                                    failingAppender, cudaAppender: null,
                                                    ex => ex is SimulatedEpUnavailableException
                                                   );

        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.ActiveProvider);
        Assert.Equal(OnnxExecutionProvider.DirectMl, capabilities.RequestedProvider);
        Assert.NotNull(capabilities.LastLoadWarning);
        Assert.Contains("no DX12 device", capabilities.LastLoadWarning);
    }

    [Fact]
    public void ConfigureWithInjectedDmlAppenderThatSucceedsRecordsDirectMl()
    {
#if !USE_GPU
        // On CPU-only builds the OnnxRuntimeCapabilities invariant rejects
        // actual=DirectMl because it isn't in CompiledInProviders. The
        // delegate path can still succeed at the appender level — but the
        // recording layer (issue #18) refuses to lie about what loaded.
        // The test confirms that contract.
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();
        Action<SessionOptions, int> noopAppender = (_, _) => { };

        Assert.Throws<InvalidOperationException>(() =>
            OnnxExecutionProviderConfigurator.Configure(options, OnnxExecutionProvider.DirectMl,
                                                        capabilities, NullLogger.Instance,
                                                        noopAppender, cudaAppender: null
                                                       )
        );
#else
        // On GPU builds DirectMl is in CompiledInProviders, so the no-op
        // appender path records the success cleanly.
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();
        Action<SessionOptions, int> noopAppender = (_, _) => { };

        OnnxExecutionProviderConfigurator.Configure(options, OnnxExecutionProvider.DirectMl,
                                                    capabilities, NullLogger.Instance,
                                                    noopAppender, cudaAppender: null
                                                   );

        Assert.Equal(OnnxExecutionProvider.DirectMl, capabilities.ActiveProvider);
        Assert.Equal(OnnxExecutionProvider.DirectMl, capabilities.RequestedProvider);
        Assert.Null(capabilities.LastLoadWarning);
#endif
    }

    [Fact]
    public void ConfigureWithInjectedCudaAppenderThatThrowsFallsBackToCpuAndWarns()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();
        Action<SessionOptions, int> failingAppender = (_, _) =>
            throw new SimulatedEpUnavailableException("CUDA driver too old");

        OnnxExecutionProviderConfigurator.Configure(options, OnnxExecutionProvider.Cuda,
                                                    capabilities, NullLogger.Instance,
                                                    dmlAppender: null, failingAppender,
                                                    ex => ex is SimulatedEpUnavailableException
                                                   );

        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.ActiveProvider);
        Assert.Equal(OnnxExecutionProvider.Cuda, capabilities.RequestedProvider);
        Assert.NotNull(capabilities.LastLoadWarning);
        Assert.Contains("CUDA driver too old", capabilities.LastLoadWarning);
    }

    [Fact]
    public void ConfigureRethrowsNonRecoverableExceptionsFromAppender()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();
        Action<SessionOptions, int> brokenAppender = (_, _) =>
            throw new DllNotFoundException("DirectML.dll missing");

        // DllNotFoundException is a deployment defect, not an EP-unavailable
        // signal — the production predicate (and our test's narrow one) must
        // not silently downgrade.
        Assert.Throws<DllNotFoundException>(() =>
            OnnxExecutionProviderConfigurator.Configure(options, OnnxExecutionProvider.DirectMl,
                                                        capabilities, NullLogger.Instance,
                                                        brokenAppender, cudaAppender: null,
                                                        ex => ex is SimulatedEpUnavailableException
                                                       )
        );
    }

    [Theory]
    [InlineData(typeof(DllNotFoundException))]
    [InlineData(typeof(BadImageFormatException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    public void ProductionIsRecoverablePredicateRejectsDeploymentAndProgrammerErrors(Type exceptionType)
    {
        // This test calls the internal Configure overload with isRecoverable=null,
        // which selects the production IsRecoverableEpAppendFailure predicate
        // (ex is OnnxRuntimeException). Each exception type below is a known
        // deployment or programmer error that must NOT be silently downgraded
        // to CPU; if the production predicate were "simplified" to accept any
        // Exception, these would silently fall through and the test catches it.
        // The positive case (OnnxRuntimeException → caught) cannot be unit-
        // tested in isolation because that exception's constructor is internal
        // to the Microsoft.ML.OnnxRuntime package; Phase 4 manual verification
        // against real DirectML hardware covers it implicitly.
        var capabilities = new OnnxRuntimeCapabilities();
        var options = new SessionOptions();
        var instance = Activator.CreateInstance(exceptionType, ProductionPredicateProbeMessage);
        if (instance is not Exception expected)
            throw new InvalidOperationException(
                $"Activator.CreateInstance({exceptionType.Name}, string) did not return an Exception."
            );

        Action<SessionOptions, int> brokenAppender = (_, _) => throw expected;

        var ex = Assert.Throws(exceptionType, () =>
            OnnxExecutionProviderConfigurator.Configure(options, OnnxExecutionProvider.DirectMl,
                                                        capabilities, NullLogger.Instance,
                                                        brokenAppender, cudaAppender: null,
                                                        isRecoverable: null
                                                       )
        );
        Assert.Contains(ProductionPredicateProbeMessage, ex.Message);
    }

    private const string ProductionPredicateProbeMessage = "production-predicate-probe";

    /// <summary>
    ///     Stand-in for OnnxRuntimeException (whose constructor is
    ///     internal to Microsoft.ML.OnnxRuntime). The injected
    ///     isRecoverable predicate in the test treats this type the
    ///     same way the production predicate treats
    ///     OnnxRuntimeException: caught, fall back to CPU.
    /// </summary>
    private sealed class SimulatedEpUnavailableException : Exception
    {
        public SimulatedEpUnavailableException(string message) : base(message) { }
    }
}
