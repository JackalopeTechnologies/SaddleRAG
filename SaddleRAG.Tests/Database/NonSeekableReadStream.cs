// NonSeekableReadStream.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tests.Database;

internal sealed class NonSeekableReadStream : Stream
{
    public NonSeekableReadStream(Stream source)
    {
        mSource = source;
    }

    private readonly Stream mSource;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => mSource.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => mSource.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer,
                                        int offset,
                                        int count,
                                        CancellationToken cancellationToken) =>
        mSource.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer,
                                             CancellationToken cancellationToken = default) =>
        mSource.ReadAsync(buffer, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            mSource.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await mSource.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
