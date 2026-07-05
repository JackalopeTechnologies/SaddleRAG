// OnnxRuntimeCapabilitiesDeviceLossTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxRuntimeCapabilitiesDeviceLossTests
{
    [Fact]
    public void RecoveryIncrementsCountAndStampsTimestamp()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        DateTime before = DateTime.UtcNow;

        capabilities.RecordDeviceLossRecovery();
        capabilities.RecordDeviceLossRecovery();

        Assert.Equal(expected: 2, capabilities.DeviceLossRecoveryCount);
        Assert.NotNull(capabilities.LastDeviceLossUtc);
        Assert.InRange(capabilities.LastDeviceLossUtc.Value, before, DateTime.UtcNow);
        Assert.False(capabilities.DeviceLossFallbackActive);
    }

    [Fact]
    public void FallbackToCpuSetsDegradedState()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        capabilities.RecordDeviceLossFallbackToCpu("GPU device lost twice; embedding session now on CPU.");

        Assert.True(capabilities.DeviceLossFallbackActive);
        Assert.Equal(OnnxExecutionProvider.Cpu, capabilities.ActiveProvider);
        Assert.Contains("CPU", capabilities.LastLoadWarning);
    }

    [Fact]
    public void FallbackWithoutWarningThrows()
    {
        var capabilities = new OnnxRuntimeCapabilities();

        Assert.ThrowsAny<ArgumentException>(() => capabilities.RecordDeviceLossFallbackToCpu(string.Empty));
    }
}
