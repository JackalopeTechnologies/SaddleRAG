// ManifestBuilder.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Security.Cryptography;

#endregion

namespace SaddleRAG.Packaging.Internal;

/// <summary>
///     Accumulates per-blob sha256 + byte-count as the exporter streams
///     payloads into the zip. The exporter wraps each blob's stream with
///     <see cref="OpenBlob" /> and writes through the returned wrapper;
///     <see cref="GetBlob" /> retrieves the descriptor once writing
///     finishes.
/// </summary>
public sealed class ManifestBuilder
{
    private readonly Dictionary<string, BlobInfo> mBlobs = new(StringComparer.Ordinal);

    /// <summary>
    ///     Opens a write-only hashing sink for <paramref name="path" />.
    ///     Disposing the returned stream records the sha256 and byte-count.
    /// </summary>
    public Stream OpenBlob(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new HashingSink(this, path);
    }

    /// <summary>
    ///     Returns the recorded <see cref="BlobInfo" /> for <paramref name="path" />.
    ///     Throws if the blob has not yet been closed.
    /// </summary>
    public BlobInfo GetBlob(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!mBlobs.TryGetValue(path, out var info))
            throw new InvalidOperationException($"No blob recorded at '{path}'");
        return info;
    }

    /// <summary>
    ///     Returns a read-only view of all recorded blobs keyed by path.
    /// </summary>
    public IReadOnlyDictionary<string, BlobInfo> ToDictionary() => mBlobs;

    private void RecordBlob(string path, byte[] hash, long bytes)
    {
        var info = new BlobInfo
                       {
                           Sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
                           Bytes = bytes
                       };
        mBlobs[path] = info;
    }

    private sealed class HashingSink : Stream
    {
        public HashingSink(ManifestBuilder owner, string path)
        {
            mOwner = owner;
            mPath = path;
            mHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        private readonly ManifestBuilder mOwner;
        private readonly string mPath;
        private readonly IncrementalHash mHasher;
        private long mBytes;
        private bool mDisposed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => mBytes;
        public override long Position { get => mBytes; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            mHasher.AppendData(buffer, offset, count);
            mBytes += count;
        }

#pragma warning disable STR0010 // Stream override methods cannot validate struct/span parameters
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            mHasher.AppendData(buffer);
            mBytes += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
#pragma warning restore STR0010

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer, offset, count);
            return Task.CompletedTask;
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
                if (disposing)
                {
                    var hash = mHasher.GetHashAndReset();
                    mOwner.RecordBlob(mPath, hash, mBytes);
                    mHasher.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
