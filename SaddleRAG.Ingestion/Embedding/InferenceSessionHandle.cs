// InferenceSessionHandle.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.ML.OnnxRuntime;

#endregion

namespace SaddleRAG.Ingestion.Embedding;

/// <summary>
///     Production <see cref="IOnnxSessionHandle" /> wrapping a real
///     <see cref="InferenceSession" />.
/// </summary>
internal sealed class InferenceSessionHandle : IOnnxSessionHandle
{
    public InferenceSessionHandle(InferenceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        mSession = session;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, NodeMetadata> InputMetadata => mSession.InputMetadata;

    /// <inheritdoc />
    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        return mSession.Run(inputs);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        mSession.Dispose();
    }

    private readonly InferenceSession mSession;
}
