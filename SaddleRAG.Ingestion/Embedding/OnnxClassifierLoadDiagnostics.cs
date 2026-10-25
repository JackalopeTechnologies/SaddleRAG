// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>Retains native startup cautions and classifies only a complete, successful, known Phi-3 load.</summary>
internal sealed partial class OnnxClassifierLoadDiagnostics : IDisposable
{
    internal OnnxClassifierLoadDiagnostics(bool eligible, ILogger? logger)
    {
        mEligible = eligible;
        mLogger = logger;
        LogId = LogIdPrefix + Guid.NewGuid().ToString(GuidFormat);
        if (!smActive.TryAdd(LogId, this))
            throw new InvalidOperationException(DuplicateScopeMessage);
    }

    internal string LogId { get; }

    internal static OnnxClassifierLoadDiagnostics Begin(ClassifierModelEntry entry, string modelDirectory, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(modelDirectory);
        bool eligible = false;
#if USE_GPU && !USE_GPU_CUDA
        try
        {
            string configuration = File.ReadAllText(Path.Combine(modelDirectory,
                OnnxClassifierDiagnosticEligibility.ConfigurationFilename));
            if (OnnxClassifierDiagnosticEligibility.IsEligible(entry, configuration,
                    OnnxClassifierDiagnosticEligibility.ReviewedGraphSha256))
                eligible = OnnxClassifierDiagnosticEligibility.IsEligible(entry, configuration,
                    OnnxClassifierDiagnosticEligibility.ReadGraphFingerprint(modelDirectory));
        }
        catch (IOException)
        {
            // The model constructor reports missing or unreadable model files; preserve its diagnostics.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostic classification must not replace the native model-load failure.
        }
        catch (CryptographicException)
        {
            // An unavailable fingerprint leaves every native warning at its original severity.
        }
#endif
        return new OnnxClassifierLoadDiagnostics(eligible, logger);
    }

    internal string CreateConfigurationOverlay()
    {
        var overlay = new JsonObject
        {
            [OnnxClassifierDiagnosticEligibility.ModelKey] = new JsonObject
            {
                [OnnxClassifierDiagnosticEligibility.DecoderKey] = new JsonObject
                {
                    [OnnxClassifierDiagnosticEligibility.SessionOptionsKey] = new JsonObject
                    {
                        [OnnxClassifierDiagnosticEligibility.LogIdKey] = LogId
                    }
                }
            }
        };
        return overlay.ToJsonString();
    }

    internal void Complete(bool reviewedRuntime)
    {
        lock (mGate)
        {
            ObjectDisposedException.ThrowIf(mDisposed, this);
            mCompleted = reviewedRuntime;
        }
    }

    internal static bool TryReceive(OrtLoggingLevel severity, string category, string logId,
        string location, string message)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(logId);
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(message);
        bool received = smActive.TryGetValue(logId, out OnnxClassifierLoadDiagnostics? scope) &&
                        scope.Receive(severity, category, location, message);
        return received;
    }

    private bool Receive(OrtLoggingLevel severity, string category, string location, string message)
    {
        bool received;
        bool buffered = false;
        lock (mGate)
        {
            received = !mDisposed;
            if (received)
            {
                bool candidate = IsExpectedDiagnostic(severity, category, location, message);
                string expectedMessage = mPending.Count switch
                {
                    0 => ShapeMessage,
                    1 => PlacementMessage,
                    2 => VerboseMessage,
                    _ => string.Empty
                };
                buffered = candidate && message == expectedMessage;
                if (buffered)
                    mPending.Add((severity, category, location, message));
                if (OnnxRuntimeDiagnostics.Classify(severity, category, location, message) >= LogLevel.Warning && !buffered)
                    mUnexpectedDiagnostic = true;
            }
        }
        if (received && !buffered)
            OnnxRuntimeDiagnostics.Emit(mLogger, OnnxRuntimeDiagnostics.Classify(severity, category, location, message),
                category, LogId, location, message);
        return received;
    }

    public void Dispose()
    {
        List<(OrtLoggingLevel Severity, string Category, string Location, string Message)> pending = [];
        bool classifyAsInformation = false;
        lock (mGate)
        {
            if (!mDisposed)
            {
                mDisposed = true;
                smActive.TryRemove(LogId, out _);
                classifyAsInformation = mEligible && mCompleted && !mUnexpectedDiagnostic &&
                                        mPending.Count == ExpectedDiagnosticCount;
                pending.AddRange(mPending);
                mPending.Clear();
            }
        }
        foreach (var diagnostic in pending)
        {
            LogLevel level = classifyAsInformation ? LogLevel.Information :
                OnnxRuntimeDiagnostics.Classify(diagnostic.Severity, diagnostic.Category, diagnostic.Location, diagnostic.Message);
            OnnxRuntimeDiagnostics.Emit(mLogger, level, diagnostic.Category, LogId, diagnostic.Location, diagnostic.Message);
        }
    }

    private static bool IsExpectedDiagnostic(OrtLoggingLevel severity, string category, string location, string message)
    {
        bool expected = severity == OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING && category == NativeCategory &&
                        ((message == ShapeMessage && ShapeLocation().IsMatch(location)) ||
                         ((message == PlacementMessage || message == VerboseMessage) && PlacementLocation().IsMatch(location)));
        return expected;
    }

    [GeneratedRegex(ShapeLocationPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ShapeLocation();

    [GeneratedRegex(PlacementLocationPattern, RegexOptions.CultureInvariant)]
    private static partial Regex PlacementLocation();

    private static readonly ConcurrentDictionary<string, OnnxClassifierLoadDiagnostics> smActive = new(StringComparer.Ordinal);
    private readonly Lock mGate = new();
    private readonly bool mEligible;
    private readonly ILogger? mLogger;
    private readonly List<(OrtLoggingLevel Severity, string Category, string Location, string Message)> mPending = [];
    private bool mCompleted;
    private bool mDisposed;
    private bool mUnexpectedDiagnostic;
    private const int ExpectedDiagnosticCount = 3;
    private const string NativeCategory = "onnxruntime";
    private const string LogIdPrefix = "SaddleRAG.Classifier.";
    private const string GuidFormat = "N";
    private const string DuplicateScopeMessage = "A classifier diagnostic scope already uses the generated session identifier.";
    private const string ShapeLocationPattern = @"(?:^|[/\\])inference_session\.cc:\d+ onnxruntime::InferenceSession::Initialize$";
    private const string PlacementLocationPattern = @"(?:^|[/\\])session_state\.cc:\d+ onnxruntime::VerifyEachNodeIsAssignedToAnEp$";

    // ORT 1.24.4 emits this caution only after its graph-capture placement checks pass.
    // GenAI 0.14.1 uses fixed masks/shared caches and captures the one-token decoder phase.
    // These latest DirectML packages still emit the diagnostic. Retain the full caution at
    // Information only for the reviewed configuration and successful complete startup batch;
    // this is a scoped third-party diagnostic classification, not a change to graph safety.
    // https://github.com/microsoft/onnxruntime/blob/2d924974ef147392ced8409d36bd6d2e7fcc8a74/onnxruntime/core/session/inference_session.cc#L2408-L2451
    internal const string ShapeMessage = "This model has shape massaging nodes that will execute on CPU. Use the graph capture feature with caution. As long as the intermediate shapes produced in the model using the representative input used to capture the graph, will match the shapes produced in the model for other inputs of the same shape as the representative input (common case), it is safe to use the graph capture feature.";
    internal const string PlacementMessage = "Some nodes were not assigned to the preferred execution providers which may or may not have an negative impact on performance. e.g. ORT explicitly assigns shape related ops to CPU to improve perf.";
    internal const string VerboseMessage = "Rerunning with verbose output on a non-minimal build will show node assignments.";
}
