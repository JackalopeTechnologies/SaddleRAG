// OnnxExecutionProviderConfigurator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SaddleRAG.Core.Enums;

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
///     The internal <see cref="Configure(SessionOptions, OnnxExecutionProvider,
///     OnnxRuntimeCapabilities, ILogger, Action{SessionOptions, int}?,
///     Action{SessionOptions, int}?)" /> overload accepts injectable
///     append delegates so tests can exercise the runtime-exception
///     fallback path without needing actual ORT GPU bindings.
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
    public static OnnxExecutionProvider Configure(SessionOptions options,
                                                  OnnxExecutionProvider requested,
                                                  OnnxRuntimeCapabilities capabilities,
                                                  ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(logger);

        return Configure(options, requested, capabilities, logger,
                         smDefaultDmlAppender, smDefaultCudaAppender
                        );
    }

    /// <summary>
    ///     Test-friendly overload that accepts injectable EP-append
    ///     delegates. Production callers should use the four-arg overload
    ///     which forwards to the real <see cref="SessionOptions" />
    ///     methods (gated on <c>USE_GPU</c>). Tests pass delegates that
    ///     throw <see cref="OnnxRuntimeException" /> to exercise the
    ///     runtime-fallback branch otherwise unreachable from a CPU-only
    ///     test build.
    /// </summary>
    internal static OnnxExecutionProvider Configure(SessionOptions options,
                                                    OnnxExecutionProvider requested,
                                                    OnnxRuntimeCapabilities capabilities,
                                                    ILogger logger,
                                                    Action<SessionOptions, int>? dmlAppender,
                                                    Action<SessionOptions, int>? cudaAppender,
                                                    Func<Exception, bool>? isRecoverable = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(logger);

        Func<Exception, bool> recoverable = isRecoverable ?? IsRecoverableEpAppendFailure;

        (OnnxExecutionProvider actual, string? warning) = requested switch
        {
            OnnxExecutionProvider.Cpu => (OnnxExecutionProvider.Cpu, (string?) null),
            OnnxExecutionProvider.DirectMl => TryAppendGpuProvider(
                options, OnnxExecutionProvider.DirectMl, dmlAppender, recoverable, logger
            ),
            OnnxExecutionProvider.Cuda => TryAppendGpuProvider(
                options, OnnxExecutionProvider.Cuda, cudaAppender, recoverable, logger
            ),
            var _ => HandleUnknown(requested, logger)
        };

        capabilities.RecordLoadOutcome(requested, actual, warning);
        return actual;
    }

    private static (OnnxExecutionProvider Actual, string? Warning) HandleUnknown(OnnxExecutionProvider requested,
                                                                                  ILogger logger)
    {
        string warn = string.Format(UnknownProviderWarningFormat, requested);
        logger.LogWarning("OnnxExecutionProviderConfigurator: {Warning}", warn);
        (OnnxExecutionProvider Actual, string? Warning) result = (OnnxExecutionProvider.Cpu, warn);
        return result;
    }

    private static (OnnxExecutionProvider Actual, string? Warning) TryAppendGpuProvider(
        SessionOptions options,
        OnnxExecutionProvider provider,
        Action<SessionOptions, int>? appender,
        Func<Exception, bool> isRecoverable,
        ILogger logger)
    {
        (OnnxExecutionProvider Actual, string? Warning) result;
        if (appender == null)
        {
            string warn = string.Format(GpuNotCompiledInWarningFormat, provider);
            logger.LogWarning("OnnxExecutionProviderConfigurator: {Warning}", warn);
            result = (OnnxExecutionProvider.Cpu, warn);
        }
        else
        {
            try
            {
                appender(options, DefaultGpuDeviceId);
                logger.LogInformation("OnnxExecutionProviderConfigurator: {Provider} EP appended (device {DeviceId}).",
                                      provider, DefaultGpuDeviceId
                                     );
                result = (provider, null);
            }
            catch(Exception ex) when(isRecoverable(ex))
            {
                string warn = string.Format(GpuAppendFailedWarningFormat, provider, ex.Message);
                logger.LogError(ex, "OnnxExecutionProviderConfigurator: {Warning}", warn);
                result = (OnnxExecutionProvider.Cpu, warn);
            }
        }
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

#if USE_GPU && !USE_GPU_CUDA
    // DirectML build (Windows): both DML and CUDA appenders compile. CUDA will fall
    // back gracefully at runtime on non-CUDA machines via OnnxRuntimeException.
    private static readonly Action<SessionOptions, int>? smDefaultDmlAppender =
        (opts, deviceId) => opts.AppendExecutionProvider_DML(deviceId);

    private static readonly Action<SessionOptions, int>? smDefaultCudaAppender =
        (opts, deviceId) => opts.AppendExecutionProvider_CUDA(deviceId);
#elif USE_GPU_CUDA
    // CUDA build (Linux): AppendExecutionProvider_DML is Windows-only and not compiled
    // in — the DirectML EP yields a "not compiled in" warning and CPU fallback.
    private static readonly Action<SessionOptions, int>? smDefaultDmlAppender = null;

    private static readonly Action<SessionOptions, int>? smDefaultCudaAppender =
        (opts, deviceId) => opts.AppendExecutionProvider_CUDA(deviceId);
#else
    private static readonly Action<SessionOptions, int>? smDefaultDmlAppender = null;
    private static readonly Action<SessionOptions, int>? smDefaultCudaAppender = null;
#endif

    private const string UnknownProviderWarningFormat =
        "Unknown OnnxExecutionProvider '{0}'; falling back to CPU.";

    private const string GpuAppendFailedWarningFormat =
        "Failed to append {0} execution provider ({1}); falling back to CPU.";

    private const string GpuNotCompiledInWarningFormat =
        "ExecutionProvider '{0}' requested but this build is CPU-only (UseGpu=false at build time); falling back to CPU.";

    private const int DefaultGpuDeviceId = 0;
}
