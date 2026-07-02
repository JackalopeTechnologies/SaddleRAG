// SerializedClassifierGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Decorator over an <see cref="IClassifierGenerator" /> that guarantees at
///     most one in-flight <see cref="GenerateAsync" /> against the wrapped
///     generator. onnxruntime-genai native <c>Generator</c> creation over a
///     shared <c>Model</c> is not thread-safe: overlapping generate calls from
///     the scrape classify stage, reextract, and finalizer produced recurring
///     0xc0000005 access violations in <c>OgaCreateGenerator</c> that killed
///     the whole service process (issue #135). Every composition root that
///     wires <see cref="OnnxClassifierGenerator" /> must wrap it in this
///     decorator; classification throughput against a single local model is
///     effectively serial anyway, so the semaphore costs nothing in practice.
/// </summary>
public sealed class SerializedClassifierGenerator : IClassifierGenerator, IDisposable
{
    /// <summary>
    ///     Wraps <paramref name="inner" /> so its <see cref="GenerateAsync" />
    ///     calls never overlap.
    /// </summary>
    /// <param name="inner">The generator whose calls must be serialized.</param>
    public SerializedClassifierGenerator(IClassifierGenerator inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        mInner = inner;
    }

    private readonly IClassifierGenerator mInner;
    private readonly SemaphoreSlim mGenerateLock = new(1, 1);
    private bool mDisposed;

    /// <inheritdoc />
    public string ModelId => mInner.ModelId;

    /// <inheritdoc />
    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        ObjectDisposedException.ThrowIf(mDisposed, this);

        await mGenerateLock.WaitAsync(ct);

        string res;

        try
        {
            res = await mInner.GenerateAsync(prompt, ct);
        }
        finally
        {
            mGenerateLock.Release();
        }

        return res;
    }

    public void Dispose()
    {
        if (!mDisposed)
        {
            mDisposed = true;
            mGenerateLock.Dispose();
        }
    }
}
