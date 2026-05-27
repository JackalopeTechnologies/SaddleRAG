// TeeStream.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Packaging.Internal;

/// <summary>
///     Write-only stream that tees every write into two underlying streams.
///     The exporter uses this to write a zip entry and a hashing sink
///     in lockstep so the manifest sha256 is computed inline with the
///     bytes that actually land in the bundle.
/// </summary>
internal sealed class TeeStream : Stream
{
    public TeeStream(Stream primary, Stream secondary, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        mPrimary = primary;
        mSecondary = secondary;
        mLeaveOpen = leaveOpen;
    }

    private readonly Stream mPrimary;
    private readonly Stream mSecondary;
    private readonly bool mLeaveOpen;
    private long mPosition;
    private bool mDisposed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => mPosition;
    public override long Position { get => mPosition; set => throw new NotSupportedException(); }

    public override void Flush()
    {
        mPrimary.Flush();
        mSecondary.Flush();
    }

    public override async Task FlushAsync(CancellationToken ct)
    {
        await mPrimary.FlushAsync(ct);
        await mSecondary.FlushAsync(ct);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        mPrimary.Write(buffer, offset, count);
        mSecondary.Write(buffer, offset, count);
        mPosition += count;
    }

#pragma warning disable STR0010 // Stream override methods cannot validate struct/span parameters
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        mPrimary.Write(buffer);
        mSecondary.Write(buffer);
        mPosition += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await mPrimary.WriteAsync(buffer, ct);
        await mSecondary.WriteAsync(buffer, ct);
        mPosition += buffer.Length;
    }
#pragma warning restore STR0010

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        await mPrimary.WriteAsync(buffer.AsMemory(offset, count), ct);
        await mSecondary.WriteAsync(buffer.AsMemory(offset, count), ct);
        mPosition += count;
    }

#pragma warning disable STR0010 // Read is not supported; throw before any access is meaningful
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
#pragma warning restore STR0010
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!mDisposed)
        {
            mDisposed = true;
            if (disposing && !mLeaveOpen)
            {
                mPrimary.Dispose();
                mSecondary.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
