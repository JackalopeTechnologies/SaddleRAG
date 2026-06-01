// ClassifierBackendSwitch.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Singleton <see cref="ILlmClassifier" /> that delegates every
///     <see cref="ClassifyAsync" /> call to whichever backend is currently
///     active. Defaults to the ONNX backend at construction; calling
///     <see cref="UseOllamaAsync" /> switches at runtime after verifying
///     Ollama is reachable. Calling <see cref="UseOnnx" /> switches back.
///     The backend swap is atomic via a <see langword="volatile" /> reference
///     field: .NET guarantees that a reference write/read is a single
///     indivisible operation on all supported hardware, and
///     <see langword="volatile" /> ensures the new value is visible to all
///     threads immediately.
/// </summary>
public sealed class ClassifierBackendSwitch : ILlmClassifier
{
    /// <summary>
    ///     Initializes a new <see cref="ClassifierBackendSwitch" /> with ONNX
    ///     as the default active backend.
    /// </summary>
    /// <param name="onnx">The ONNX-backed classifier (always available).</param>
    /// <param name="ollama">The Ollama-backed classifier (optional at runtime).</param>
    /// <param name="probe">Reachability check for the Ollama endpoint.</param>
    /// <param name="logger">Logger for backend-switch events.</param>
    public ClassifierBackendSwitch(OnnxLlmClassifier onnx,
                                   ILlmClassifier ollama,
                                   IOllamaProbe probe,
                                   ILogger<ClassifierBackendSwitch> logger)
    {
        ArgumentNullException.ThrowIfNull(onnx);
        ArgumentNullException.ThrowIfNull(ollama);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(logger);

        mOnnx = onnx;
        mOllama = ollama;
        mProbe = probe;
        mLogger = logger;
        mActive = onnx;
    }

    private readonly OnnxLlmClassifier mOnnx;
    private readonly ILlmClassifier mOllama;
    private readonly IOllamaProbe mProbe;
    private readonly ILogger<ClassifierBackendSwitch> mLogger;

    // volatile: reference writes are already atomic in .NET, but volatile
    // prevents CPU/compiler reordering so every thread sees the latest value.
    private volatile ILlmClassifier mActive;

    /// <inheritdoc />
    public string BackendName => mActive.BackendName;

    /// <inheritdoc />
    public string ModelId => mActive.ModelId;

    /// <inheritdoc />
    public string GetCurrentVersion() => mActive.GetCurrentVersion();

    /// <summary>
    ///     Name of the currently active backend: <c>"onnx"</c> or <c>"ollama"</c>.
    ///     Retained for existing health/status callers; equals <see cref="BackendName" />.
    /// </summary>
    public string ActiveBackendName => BackendName;

    /// <summary>
    ///     Switch to the ONNX backend. Always succeeds immediately.
    /// </summary>
    public void UseOnnx()
    {
        mActive = mOnnx;
        mLogger.LogInformation("Classifier backend switched to {Backend}", ClassifierBackendNames.Onnx);
    }

    /// <summary>
    ///     Switch to the Ollama backend after verifying it is reachable.
    ///     Throws <see cref="InvalidOperationException" /> with an actionable
    ///     message if Ollama is not running. The active backend is NOT changed
    ///     when the check fails, so the caller stays on ONNX in that case.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">
    ///     Ollama is not reachable at the configured endpoint.
    /// </exception>
    public async Task UseOllamaAsync(CancellationToken ct = default)
    {
        bool reachable = await mProbe.IsReachableAsync(ct);

        if (!reachable)
            throw new InvalidOperationException(OllamaNotReachableMessage);

        mActive = mOllama;
        mLogger.LogInformation("Classifier backend switched to {Backend}", ClassifierBackendNames.Ollama);
    }

    /// <inheritdoc />
    public Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                        string libraryHint,
                                                                        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(libraryHint);

        return mActive.ClassifyAsync(page, libraryHint, ct);
    }

    private const string OllamaNotReachableMessage = "Cannot switch to Ollama classifier: Ollama is not reachable. Install and run Ollama from https://ollama.com, then retry.";
}
