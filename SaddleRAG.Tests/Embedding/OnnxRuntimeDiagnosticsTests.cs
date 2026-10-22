// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxRuntimeDiagnosticsTests
{
    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("829088475")]
    public void ExpectedNativeGraphReplacementIsDiagnostic(string value)
    {
        string message = $"Config with key [gpu_graph_id] already exists with value [{value}]. It will be overwritten";
        Assert.Equal(LogLevel.Debug, OnnxRuntimeDiagnostics.Classify(
            OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING, "onnxruntime", NativeLocation, message));
    }

    [Theory]
    [InlineData("gpu_graph_id", "invalid", NativeLocation, "onnxruntime")]
    [InlineData("gpu_graph_id", "-2", NativeLocation, "onnxruntime")]
    [InlineData("gpu_graph_id", "2147483648", NativeLocation, "onnxruntime")]
    [InlineData("other_setting", "42", NativeLocation, "onnxruntime")]
    [InlineData("gpu_graph_id", "42", "application.cs:45 ConfigOptions::AddConfigEntry", "onnxruntime")]
    [InlineData("gpu_graph_id", "42", NativeLocation, "application")]
    public void DifferentNativeWarningsRetainTheirSeverity(string key, string value, string location, string category)
    {
        string message = $"Config with key [{key}] already exists with value [{value}]. It will be overwritten";
        Assert.Equal(LogLevel.Warning, OnnxRuntimeDiagnostics.Classify(
            OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING, category, location, message));
    }

    [Theory]
    [InlineData(OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, LogLevel.Error)]
    [InlineData(OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL, LogLevel.Critical)]
    public void ErrorsWithTheSameTextAreNeverDowngraded(OrtLoggingLevel severity, LogLevel expected)
    {
        const string message = "Config with key [gpu_graph_id] already exists with value [42]. It will be overwritten";
        Assert.Equal(expected, OnnxRuntimeDiagnostics.Classify(severity, "onnxruntime", NativeLocation, message));
    }

    [Fact]
    public void DeviceLossWarningRemainsVisible()
    {
        Assert.Equal(LogLevel.Warning, OnnxRuntimeDiagnostics.Classify(
            OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING, "onnxruntime", NativeLocation,
            "DXGI_ERROR_DEVICE_REMOVED"));
    }

    private const string NativeLocation = "config_options.cc:45 onnxruntime::ConfigOptions::AddConfigEntry";
}
