// ArtifactHashingReadStream.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;

namespace SaddleRAG.Database.Repositories;

/// <summary>
///     Read-only forwarding stream that computes SHA-256 and byte length as
///     its source is consumed. It deliberately does not require seeking and
///     does not own the caller's source stream.
/// </summary>
internal sealed class ArtifactHashingReadStream : Stream
{
    public ArtifactHashingReadStream(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("Source stream must be readable.", nameof(source));
        mSource = source;
        mHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    private readonly IncrementalHash mHash;
    private readonly Stream mSource;
    private string? mCompletedHash;

    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public string GetSha256()
    {
        mCompletedHash ??= Convert.ToHexStringLower(mHash.GetHashAndReset());
        return mCompletedHash;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var result = mSource.Read(buffer, offset, count);
        RecordRead(buffer.AsSpan(offset, result));
        return result;
    }

    public override int Read(Span<byte> buffer)
    {
        var result = mSource.Read(buffer);
        RecordRead(buffer[..result]);
        return result;
    }

    public override async Task<int> ReadAsync(byte[] buffer,
                                              int offset,
                                              int count,
                                              CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var result = await mSource.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        RecordRead(buffer.AsSpan(offset, result));
        return result;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
                                                   CancellationToken cancellationToken = default)
    {
        var result = await mSource.ReadAsync(buffer, cancellationToken);
        RecordRead(buffer.Span[..result]);
        return result;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            mHash.Dispose();
        base.Dispose(disposing);
    }

    private void RecordRead(ReadOnlySpan<byte> bytes)
    {
        if (mCompletedHash != null)
            throw new InvalidOperationException("Cannot read after the artifact hash is finalized.");
        if (!bytes.IsEmpty)
        {
            mHash.AppendData(bytes);
            BytesRead += bytes.Length;
        }
    }
}
