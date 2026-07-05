// RecoverableOnnxSession.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Self-healing wrapper around an ONNX <see cref="InferenceSession" />
///     (issue #144). When a run fails with a DXGI device-loss
///     <see cref="OnnxRuntimeException" /> (GPU suspended/hung/reset), the
///     wrapper disposes the dead session, rebuilds it on the configured
///     execution provider and retries; if the device is still gone it
///     rebuilds on the CPU provider, marks the runtime degraded on
///     <see cref="OnnxRuntimeCapabilities" />, and serves the query anyway.
///     Recovered incidents log at Warning — Error is reserved for failures
///     that propagate to the caller unrecovered (the monitor's error badge
///     counts Error+, issue #143).
///     Thread-safety: every caller invokes <see cref="Run" /> while holding
///     the process-wide <see cref="OnnxInferenceGate" />, which serializes
///     runs AND rebuilds across all sessions; this class adds no locking of
///     its own and must not be called outside the gate.
/// </summary>
public sealed class RecoverableOnnxSession : IDisposable
{
    /// <summary>
    ///     Production constructor. <paramref name="primaryOptionsFactory" />
    ///     must configure the requested execution provider (and re-records
    ///     the load outcome on rebuild); <paramref name="cpuOptionsFactory" />
    ///     must return bare CPU options with no EP appended.
    /// </summary>
    public RecoverableOnnxSession(string modelPath,
                                  Func<SessionOptions> primaryOptionsFactory,
                                  Func<SessionOptions> cpuOptionsFactory,
                                  OnnxRuntimeCapabilities capabilities,
                                  ILogger logger,
                                  string sessionName)
        : this(BuildHandleFactory(modelPath, primaryOptionsFactory),
               BuildHandleFactory(modelPath, cpuOptionsFactory),
               capabilities,
               logger,
               sessionName,
               isDeviceLoss: null
              )
    {
    }

    /// <summary>
    ///     Test seam: injectable handle factories and device-loss filter
    ///     (pattern per <see cref="OnnxExecutionProviderConfigurator" />'s
    ///     internal overload).
    /// </summary>
    internal RecoverableOnnxSession(Func<IOnnxSessionHandle> primaryFactory,
                                    Func<IOnnxSessionHandle> cpuFactory,
                                    OnnxRuntimeCapabilities capabilities,
                                    ILogger logger,
                                    string sessionName,
                                    Func<Exception, bool>? isDeviceLoss)
    {
        ArgumentNullException.ThrowIfNull(primaryFactory);
        ArgumentNullException.ThrowIfNull(cpuFactory);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(sessionName);

        mPrimaryFactory = primaryFactory;
        mCpuFactory = cpuFactory;
        mCapabilities = capabilities;
        mLogger = logger;
        mSessionName = sessionName;
        mIsDeviceLoss = isDeviceLoss ?? DefaultIsDeviceLoss;
        mHandle = primaryFactory();
    }

    /// <summary>
    ///     Input metadata of the current underlying session.
    /// </summary>
    public IReadOnlyDictionary<string, NodeMetadata> InputMetadata => mHandle.InputMetadata;

    /// <summary>
    ///     Runs inference, transparently rebuilding the session after GPU
    ///     device loss. Must be called while holding
    ///     <see cref="OnnxInferenceGate" />.
    /// </summary>
    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result;
        try
        {
            result = mHandle.Run(inputs);
        }
        catch(Exception ex) when(mIsDeviceLoss(ex))
        {
            result = RecoverAndRun(inputs, ex);
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        mHandle.Dispose();
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RecoverAndRun(
        IReadOnlyCollection<NamedOnnxValue> inputs,
        Exception deviceLoss)
    {
        mLogger.LogWarning(deviceLoss,
                           "GPU device loss detected on ONNX session '{Session}'; rebuilding on the configured provider.",
                           mSessionName
                          );

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result;
        try
        {
            Rebuild(mPrimaryFactory);
            result = mHandle.Run(inputs);
            mCapabilities.RecordDeviceLossRecovery();
            mLogger.LogWarning(
                "ONNX session '{Session}' recovered after GPU device loss (rebuilt on the configured provider).",
                mSessionName
            );
        }
        catch(Exception ex) when(mIsDeviceLoss(ex))
        {
            result = FallBackToCpuAndRun(inputs, ex);
        }

        return result;
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> FallBackToCpuAndRun(
        IReadOnlyCollection<NamedOnnxValue> inputs,
        Exception secondLoss)
    {
        mLogger.LogWarning(secondLoss,
                           "ONNX session '{Session}' still device-lost after rebuild; falling back to the CPU execution provider.",
                           mSessionName
                          );

        Rebuild(mCpuFactory);
        mCapabilities.RecordDeviceLossFallbackToCpu(string.Format(CpuFallbackWarningFormat,
                                                                  mSessionName,
                                                                  secondLoss.Message
                                                                 ));

        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result = mHandle.Run(inputs);
        mCapabilities.RecordDeviceLossRecovery();
        mLogger.LogWarning("ONNX session '{Session}' recovered on CPU fallback after repeated GPU device loss.",
                           mSessionName
                          );
        return result;
    }

    /// <summary>
    ///     Disposes the current (dead) handle and builds a replacement.
    ///     A second dispose of an already-disposed handle is harmless —
    ///     <see cref="InferenceSession" /> tolerates double dispose.
    /// </summary>
    private void Rebuild(Func<IOnnxSessionHandle> factory)
    {
        mHandle.Dispose();
        mHandle = factory();
    }

    private static Func<IOnnxSessionHandle> BuildHandleFactory(string modelPath,
                                                               Func<SessionOptions> optionsFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        ArgumentNullException.ThrowIfNull(optionsFactory);

        return () => new InferenceSessionHandle(new InferenceSession(modelPath, optionsFactory()));
    }

    private static bool DefaultIsDeviceLoss(Exception ex)
    {
        bool result = ex is OnnxRuntimeException && OnnxDeviceLoss.IsDeviceLossMessage(ex.Message);
        return result;
    }

    private readonly OnnxRuntimeCapabilities mCapabilities;
    private readonly Func<IOnnxSessionHandle> mCpuFactory;
    private readonly Func<Exception, bool> mIsDeviceLoss;
    private readonly ILogger mLogger;
    private readonly Func<IOnnxSessionHandle> mPrimaryFactory;
    private readonly string mSessionName;

    private IOnnxSessionHandle mHandle;

    private const string CpuFallbackWarningFormat =
        "ONNX session '{0}' fell back to the CPU execution provider after repeated GPU device loss ({1}). Restart the service or set_execution_provider to return to GPU.";
}
