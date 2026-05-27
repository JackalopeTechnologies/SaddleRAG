// EmbeddingBlobWriter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Buffers;

#endregion

namespace SaddleRAG.Packaging.Internal;

/// <summary>
///     Writes float32 vectors of a fixed dimension to an underlying stream
///     as raw little-endian bytes. Row index in the blob matches the line
///     index in the parallel chunks.jsonl by construction — the caller
///     writes both in lockstep.
/// </summary>
public sealed class EmbeddingBlobWriter : IAsyncDisposable, IDisposable
{
    public EmbeddingBlobWriter(Stream stream, int dim, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(dim, 1);

        mStream = stream;
        mLeaveOpen = leaveOpen;
        mDim = dim;
        mBytesPerVector = checked(dim * sizeof(float));
    }

    private readonly Stream mStream;
    private readonly bool mLeaveOpen;
    private readonly int mDim;
    private readonly int mBytesPerVector;

    /// <summary>
    ///     Gets the number of dimensions each vector must have.
    /// </summary>
    public int Dimensions => mDim;

    /// <summary>
    ///     Writes a single float32 vector to the stream.
    /// </summary>
    /// <param name="vector">The vector to write. Must have exactly <see cref="Dimensions" /> elements.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteAsync(float[] vector, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length != mDim)
            throw new ArgumentException($"Vector length {vector.Length} does not match writer dimension {mDim}",
                                        nameof(vector));

        var buffer = ArrayPool<byte>.Shared.Rent(mBytesPerVector);
        try
        {
            Buffer.BlockCopy(vector, 0, buffer, 0, mBytesPerVector);
            await mStream.WriteAsync(buffer.AsMemory(0, mBytesPerVector), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await mStream.FlushAsync();
        if (!mLeaveOpen)
            await mStream.DisposeAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        mStream.Flush();
        if (!mLeaveOpen)
            mStream.Dispose();
    }
}
