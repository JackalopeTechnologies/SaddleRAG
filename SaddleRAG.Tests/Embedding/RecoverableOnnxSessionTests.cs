// RecoverableOnnxSessionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class RecoverableOnnxSessionTests
{
    [Fact]
    public void HealthyRunDelegatesWithoutRebuilding()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var handle = new FakeHandle(throwOnRun: false);
        var primaryCalls = 0;
        var session = CreateSession(() =>
                                    {
                                        primaryCalls++;
                                        return handle;
                                    },
                                    () => throw new InvalidOperationException("CPU factory must not be used"),
                                    capabilities
                                   );

        var result = session.Run([]);

        Assert.NotNull(result);
        Assert.Equal(expected: 1, primaryCalls);
        Assert.Equal(expected: 1, handle.RunCalls);
        Assert.Equal(expected: 0, capabilities.DeviceLossRecoveryCount);
    }

    [Fact]
    public void SingleDeviceLossRebuildsOnPrimaryAndRecovers()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var deadHandle = new FakeHandle(throwOnRun: true);
        var freshHandle = new FakeHandle(throwOnRun: false);
        var primaryCalls = 0;
        var session = CreateSession(() =>
                                    {
                                        primaryCalls++;
                                        return primaryCalls == 1 ? deadHandle : freshHandle;
                                    },
                                    () => throw new InvalidOperationException("CPU factory must not be used"),
                                    capabilities
                                   );

        var result = session.Run([]);

        Assert.NotNull(result);
        Assert.Equal(expected: 2, primaryCalls);
        Assert.True(deadHandle.Disposed);
        Assert.Equal(expected: 1, freshHandle.RunCalls);
        Assert.Equal(expected: 1, capabilities.DeviceLossRecoveryCount);
        Assert.False(capabilities.DeviceLossFallbackActive);
    }

    [Fact]
    public void PersistentDeviceLossFallsBackToCpu()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var cpuHandle = new FakeHandle(throwOnRun: false);
        var primaryCalls = 0;
        var cpuCalls = 0;
        var session = CreateSession(() =>
                                    {
                                        primaryCalls++;
                                        return new FakeHandle(throwOnRun: true);
                                    },
                                    () =>
                                    {
                                        cpuCalls++;
                                        return cpuHandle;
                                    },
                                    capabilities
                                   );

        var result = session.Run([]);

        Assert.NotNull(result);
        Assert.Equal(expected: 2, primaryCalls);
        Assert.Equal(expected: 1, cpuCalls);
        Assert.Equal(expected: 1, cpuHandle.RunCalls);
        Assert.True(capabilities.DeviceLossFallbackActive);
        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.ActiveProvider);
        Assert.Equal(expected: 1, capabilities.DeviceLossRecoveryCount);
    }

    [Fact]
    public void CpuFailureAfterFallbackPropagates()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var session = CreateSession(() => new FakeHandle(throwOnRun: true),
                                    () => new FakeHandle(throwOnRun: true),
                                    capabilities
                                   );

        Assert.Throws<InvalidOperationException>(() => session.Run([]));
        Assert.True(capabilities.DeviceLossFallbackActive);
        Assert.Equal(expected: 0, capabilities.DeviceLossRecoveryCount);
    }

    [Fact]
    public void NonDeviceLossExceptionsAreNotIntercepted()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var handle = new FakeHandle(throwOnRun: true, message: "unrelated failure");
        var primaryCalls = 0;
        var session = CreateSession(() =>
                                    {
                                        primaryCalls++;
                                        return handle;
                                    },
                                    () => throw new InvalidOperationException("CPU factory must not be used"),
                                    capabilities
                                   );

        Assert.Throws<InvalidOperationException>(() => session.Run([]));
        Assert.Equal(expected: 1, primaryCalls);
        Assert.Equal(expected: 0, capabilities.DeviceLossRecoveryCount);
    }

    private static RecoverableOnnxSession CreateSession(Func<IOnnxSessionHandle> primaryFactory,
                                                        Func<IOnnxSessionHandle> cpuFactory,
                                                        OnnxRuntimeCapabilities capabilities)
    {
        return new RecoverableOnnxSession(primaryFactory,
                                          cpuFactory,
                                          capabilities,
                                          NullLogger.Instance,
                                          sessionName: "test",
                                          isDeviceLoss: ex => ex.Message.Contains(SimulatedDeviceLossCode)
                                         );
    }

    private const string SimulatedDeviceLossCode = "887A0005";

    private sealed class FakeHandle : IOnnxSessionHandle
    {
        public FakeHandle(bool throwOnRun, string? message = null)
        {
            mThrowOnRun = throwOnRun;
            mMessage = message ?? $"simulated {SimulatedDeviceLossCode} device loss";
        }

        public int RunCalls { get; private set; }

        public bool Disposed { get; private set; }

        public IReadOnlyDictionary<string, NodeMetadata> InputMetadata { get; } =
            new Dictionary<string, NodeMetadata>();

        public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(
            IReadOnlyCollection<NamedOnnxValue> inputs)
        {
            RunCalls++;
            if (mThrowOnRun)
                throw new InvalidOperationException(mMessage);

            return new FakeResult();
        }

        public void Dispose()
        {
            Disposed = true;
        }

        private readonly string mMessage;
        private readonly bool mThrowOnRun;
    }

    private sealed class FakeResult : IDisposableReadOnlyCollection<DisposableNamedOnnxValue>
    {
        public int Count => 0;

        public DisposableNamedOnnxValue this[int index] =>
            throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<DisposableNamedOnnxValue> GetEnumerator()
        {
            return Enumerable.Empty<DisposableNamedOnnxValue>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Dispose()
        {
        }
    }
}
