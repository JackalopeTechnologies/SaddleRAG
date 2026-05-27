// EmbeddingBlobReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Buffers;

#endregion

namespace SaddleRAG.Packaging.Internal;

/// <summary>
///     Reads fixed-dimension float32 vectors from a stream. Each call
///     returns the next vector. Throws <see cref="EndOfStreamException" />
///     if the stream ends mid-vector — i.e. the bundle was truncated.
/// </summary>
public sealed class EmbeddingBlobReader
{
    public EmbeddingBlobReader(Stream stream, int dim)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(dim, 1);

        mStream = stream;
        mDim = dim;
        mBytesPerVector = checked(dim * sizeof(float));
    }

    private readonly Stream mStream;
    private readonly int mDim;
    private readonly int mBytesPerVector;

    /// <summary>
    ///     Gets the number of dimensions each vector will have.
    /// </summary>
    public int Dimensions => mDim;

    /// <summary>
    ///     Reads the next float32 vector from the stream.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An array of <see cref="Dimensions" /> floats.</returns>
    /// <exception cref="EndOfStreamException">Thrown if the stream ends before a full vector is read.</exception>
    public async Task<float[]> ReadAsync(CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(mBytesPerVector);
        float[] result;
        try
        {
            await mStream.ReadExactlyAsync(buffer.AsMemory(0, mBytesPerVector), ct);
            result = new float[mDim];
            Buffer.BlockCopy(buffer, 0, result, 0, mBytesPerVector);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return result;
    }
}
