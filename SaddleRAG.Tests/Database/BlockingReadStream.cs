// BlockingReadStream.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tests.Database;

internal sealed class BlockingReadStream : Stream
{
    public BlockingReadStream(Stream source)
    {
        mSource = source;
    }

    private readonly TaskCompletionSource mReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource mRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public async Task WaitForReadAsync(CancellationToken ct)
    {
        await mReadStarted.Task.WaitAsync(ct);
    }

    public void Release()
    {
        mRelease.TrySetResult();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        WaitForRelease();
        var result = mSource.Read(buffer, offset, count);
        return result;
    }

    public override int Read(Span<byte> buffer)
    {
        WaitForRelease();
        var result = mSource.Read(buffer);
        return result;
    }

    public override async Task<int> ReadAsync(byte[] buffer,
                                              int offset,
                                              int count,
                                              CancellationToken cancellationToken)
    {
        await WaitForReleaseAsync(cancellationToken);
        var result = await mSource.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        return result;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
                                                   CancellationToken cancellationToken = default)
    {
        await WaitForReleaseAsync(cancellationToken);
        var result = await mSource.ReadAsync(buffer, cancellationToken);
        return result;
    }

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

    private void WaitForRelease()
    {
        mReadStarted.TrySetResult();
        mRelease.Task.GetAwaiter().GetResult();
    }

    private async Task WaitForReleaseAsync(CancellationToken ct)
    {
        mReadStarted.TrySetResult();
        await mRelease.Task.WaitAsync(ct);
    }
}
