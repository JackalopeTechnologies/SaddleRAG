// OnnxExecutionProviderConfigurator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Configures an ONNX Runtime <see cref="SessionOptions" /> with the
///     requested execution provider (EP) and records the outcome on the
///     shared <see cref="OnnxRuntimeCapabilities" /> instance so the
///     <c>list_execution_providers</c> MCP tool can surface what actually
///     loaded.
///     CPU is always available. GPU providers (DirectML, CUDA) are gated
///     on the <c>USE_GPU</c> conditional-compilation symbol, which the
///     <c>UseGpu</c> MSBuild property defines when the GPU NuGet is
///     referenced. On a CPU-only build, requesting a GPU EP yields a
///     warning + CPU fallback rather than a hard failure — same
///     observable outcome the operator gets when the EP is compiled in
///     but the underlying hardware refuses (e.g. DirectML on a non-DX12
///     system). The native ORT side will throw at append time; we catch
///     it and fall back so the service stays up.
/// </summary>
public static class OnnxExecutionProviderConfigurator
{
    /// <summary>
    ///     Applies the requested EP to <paramref name="options" />. On
    ///     failure logs a warning and leaves <paramref name="options" />
    ///     in CPU-only state. Always records the outcome on
    ///     <paramref name="capabilities" />. Returns the EP that was
    ///     ultimately applied.
    /// </summary>
    public static string Configure(SessionOptions options,
                                   string? requested,
                                   OnnxRuntimeCapabilities capabilities,
                                   ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(logger);

        string normalized = NormalizeRequested(requested);
        bool isCpu = string.Equals(normalized, OnnxSettings.ExecutionProviderCpu, StringComparison.Ordinal);

        (string actual, string? warning) = isCpu
                                               ? (OnnxSettings.ExecutionProviderCpu, (string?) null)
                                               : AttemptNonCpuProvider(options, normalized, logger);

        capabilities.RecordLoadOutcome(normalized, actual, warning);
        return actual;
    }

    private static string NormalizeRequested(string? requested)
    {
        string trimmed = string.IsNullOrWhiteSpace(requested)
                             ? OnnxSettings.ExecutionProviderCpu
                             : requested.Trim();
        string result = trimmed.ToUpperInvariant() switch
        {
            CpuUpperKey => OnnxSettings.ExecutionProviderCpu,
            DirectMlUpperKey => OnnxSettings.ExecutionProviderDirectMl,
            CudaUpperKey => OnnxSettings.ExecutionProviderCuda,
            var _ => trimmed
        };
        return result;
    }

    private static (string Actual, string? Warning) AttemptNonCpuProvider(SessionOptions options,
                                                                          string requested,
                                                                          ILogger logger)
    {
        (string Actual, string? Warning) result = requested.ToUpperInvariant() switch
        {
            DirectMlUpperKey => TryAppendDirectMl(options, logger),
            CudaUpperKey => TryAppendCuda(options, logger),
            var _ => HandleUnknown(requested, logger)
        };
        return result;
    }

    private static (string Actual, string? Warning) HandleUnknown(string requested, ILogger logger)
    {
        string warn = string.Format(UnknownProviderWarningFormat, requested,
                                    OnnxSettings.ExecutionProviderCpu,
                                    OnnxSettings.ExecutionProviderDirectMl,
                                    OnnxSettings.ExecutionProviderCuda
                                   );
        logger.LogWarning("OnnxExecutionProviderConfigurator: {Warning}", warn);
        (string Actual, string? Warning) result = (OnnxSettings.ExecutionProviderCpu, warn);
        return result;
    }

    private static (string Actual, string? Warning) TryAppendDirectMl(SessionOptions options, ILogger logger)
    {
        (string Actual, string? Warning) result;
#if USE_GPU
        try
        {
            options.AppendExecutionProvider_DML(DefaultGpuDeviceId);
            logger.LogInformation("OnnxExecutionProviderConfigurator: DirectML EP appended (device {DeviceId}).",
                                  DefaultGpuDeviceId
                                 );
            result = (OnnxSettings.ExecutionProviderDirectMl, null);
        }
        catch(Exception ex) when(IsRecoverableEpAppendFailure(ex))
        {
            string warn = string.Format(GpuAppendFailedWarningFormat, OnnxSettings.ExecutionProviderDirectMl,
                                        ex.Message
                                       );
            logger.LogError(ex, "OnnxExecutionProviderConfigurator: {Warning}", warn);
            result = (OnnxSettings.ExecutionProviderCpu, warn);
        }
#else
        string warnCpuOnly = string.Format(GpuNotCompiledInWarningFormat,
                                           OnnxSettings.ExecutionProviderDirectMl
                                          );
        logger.LogWarning("OnnxExecutionProviderConfigurator: {Warning}", warnCpuOnly);
        result = (OnnxSettings.ExecutionProviderCpu, warnCpuOnly);
#endif
        return result;
    }

    private static (string Actual, string? Warning) TryAppendCuda(SessionOptions options, ILogger logger)
    {
        (string Actual, string? Warning) result;
#if USE_GPU
        try
        {
            options.AppendExecutionProvider_CUDA(DefaultGpuDeviceId);
            logger.LogInformation("OnnxExecutionProviderConfigurator: CUDA EP appended (device {DeviceId}).",
                                  DefaultGpuDeviceId
                                 );
            result = (OnnxSettings.ExecutionProviderCuda, null);
        }
        catch(Exception ex) when(IsRecoverableEpAppendFailure(ex))
        {
            string warn = string.Format(GpuAppendFailedWarningFormat, OnnxSettings.ExecutionProviderCuda,
                                        ex.Message
                                       );
            logger.LogError(ex, "OnnxExecutionProviderConfigurator: {Warning}", warn);
            result = (OnnxSettings.ExecutionProviderCpu, warn);
        }
#else
        string warnCpuOnly = string.Format(GpuNotCompiledInWarningFormat, OnnxSettings.ExecutionProviderCuda);
        logger.LogWarning("OnnxExecutionProviderConfigurator: {Warning}", warnCpuOnly);
        result = (OnnxSettings.ExecutionProviderCpu, warnCpuOnly);
#endif
        return result;
    }

    /// <summary>
    ///     Filter for the EP-append catch. Native ORT throws
    ///     <see cref="OnnxRuntimeException" /> when the EP itself can't bind
    ///     (no compatible GPU, missing driver, etc.) — that's the expected
    ///     fallback case. Anything else (<c>DllNotFoundException</c>,
    ///     <c>TypeInitializationException</c>, <c>OutOfMemoryException</c>,
    ///     etc.) signals a deployment defect or a programmer error and
    ///     must not be downgraded to CPU silently.
    /// </summary>
    private static bool IsRecoverableEpAppendFailure(Exception ex)
    {
        bool result = ex is OnnxRuntimeException;
        return result;
    }

    private const string UnknownProviderWarningFormat =
        "Unknown ExecutionProvider '{0}'; falling back to CPU. Valid values: {1}, {2}, {3}.";

    private const string GpuAppendFailedWarningFormat =
        "Failed to append {0} execution provider ({1}); falling back to CPU.";

    private const string GpuNotCompiledInWarningFormat =
        "ExecutionProvider '{0}' requested but this build is CPU-only (UseGpu=false at build time); falling back to CPU.";

    private const string CpuUpperKey = "CPU";
    private const string DirectMlUpperKey = "DIRECTML";
    private const string CudaUpperKey = "CUDA";
    private const int DefaultGpuDeviceId = 0;
}
