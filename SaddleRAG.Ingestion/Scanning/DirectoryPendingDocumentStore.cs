// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Disk-backed, bounded pending-document store owned by one ingestion attempt.</summary>
internal sealed class DirectoryPendingDocumentStore : IAsyncDisposable
{
    internal DirectoryPendingDocumentStore(int maxDocumentCount,
                                           long maxBytes,
                                           string? tempRoot = null)
    {
        if (maxDocumentCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDocumentCount));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        mMaxDocumentCount = maxDocumentCount;
        mMaxBytes = maxBytes;
        mTempRoot = Path.GetFullPath(tempRoot ?? Path.GetTempPath());
        ValidateTempRoot();
        RootPath = Path.Combine(mTempRoot, $"{OwnedDirectoryPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(RootPath);
        ValidateOwnedRoot();
    }

    private readonly int mMaxDocumentCount;
    private readonly long mMaxBytes;
    private readonly string mTempRoot;
    private bool mDisposed;
    private int mDocumentCount;
    private long mTotalBytes;

    internal int DocumentCount => mDocumentCount;

    internal string RootPath { get; }

    internal async Task AddAsync(PendingDirectoryDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(mDisposed, this);
        ValidateOwnedRoot();
        EnsureDocumentCapacity(document.Source.DisplayRelativePath);
        string itemPath = ItemPath(mDocumentCount);
        long payloadBytes;
        try
        {
            payloadBytes = await WriteItemAsync(itemPath,
                                                document,
                                                mMaxBytes - mTotalBytes,
                                                ct);
        }
        catch(Exception error)
        {
            try
            {
                DeletePartialItem(itemPath);
            }
            catch(Exception cleanupError)
            {
                throw new AggregateException("The pending-document spool item and its cleanup both failed.",
                                             error,
                                             cleanupError);
            }

            throw;
        }

        mDocumentCount++;
        mTotalBytes += payloadBytes;
    }

    internal async IAsyncEnumerable<PendingDirectoryDocument> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(mDisposed, this);
        ValidateOwnedRoot();
        for(var index = 0; index < mDocumentCount; index++)
        {
            ct.ThrowIfCancellationRequested();
            string itemPath = ItemPath(index);
            ValidateOrdinaryItem(itemPath);
            await using var stream = new FileStream(itemPath,
                                                    FileMode.Open,
                                                    FileAccess.Read,
                                                    FileShare.Read,
                                                    bufferSize: FileBufferSize,
                                                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            PendingDirectoryDocument? document = await JsonSerializer.DeserializeAsync<PendingDirectoryDocument>(
                                                     stream,
                                                     smJsonOptions,
                                                     ct);
            yield return document
                         ?? throw new InvalidDataException("A pending directory document could not be deserialized.");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!mDisposed)
        {
            ValidateOwnedRoot();
            for(var index = 0; index <= mDocumentCount; index++)
            {
                string itemPath = ItemPath(index);
                if (File.Exists(itemPath))
                {
                    ValidateOrdinaryItem(itemPath);
                    File.Delete(itemPath);
                }
            }

            Directory.Delete(RootPath, recursive: false);
            mDisposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private void EnsureDocumentCapacity(string relativePath)
    {
        if (mDocumentCount >= mMaxDocumentCount)
        {
            throw new DirectoryIngestionException(
                DirectoryScanReasonCodes.PendingSpoolLimitExceeded,
                PendingSpoolLimitExceededDetail,
                relativePath);
        }
    }

    private static async Task<long> WriteItemAsync(string itemPath,
                                                   PendingDirectoryDocument document,
                                                   long maximumBytes,
                                                   CancellationToken ct)
    {
        await using var stream = new FileStream(itemPath,
                                                FileMode.CreateNew,
                                                FileAccess.Write,
                                                FileShare.None,
                                                bufferSize: FileBufferSize,
                                                FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bounded = new HardBoundedWriteStream(stream,
                                                       maximumBytes,
                                                       document.Source.DisplayRelativePath);
        await JsonSerializer.SerializeAsync(bounded, document, smJsonOptions, ct);
        await bounded.FlushAsync(ct);
        return bounded.BytesWritten;
    }

    private static void DeletePartialItem(string itemPath)
    {
        if (File.Exists(itemPath))
        {
            ValidateOrdinaryItem(itemPath);
            File.Delete(itemPath);
        }
    }

    private string ItemPath(int index) => Path.Combine(RootPath, $"{index:D8}.json");

    private void ValidateOwnedRoot()
    {
        ValidateTempRoot();
        string canonicalTemp = mTempRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               + Path.DirectorySeparatorChar;
        string canonicalRoot = Path.GetFullPath(RootPath);
        string directoryName = Path.GetFileName(canonicalRoot);
        bool isOwned = canonicalRoot.StartsWith(canonicalTemp, DirectoryPathIdentity.Platform.Comparison)
                       && directoryName.StartsWith(OwnedDirectoryPrefix, StringComparison.Ordinal);
        if (!isOwned || !Directory.Exists(canonicalRoot))
            throw new InvalidOperationException("The pending-document spool is not an owned temporary directory.");
        FileAttributes attributes = File.GetAttributes(canonicalRoot);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("The pending-document spool directory cannot be a reparse point.");
    }

    private void ValidateTempRoot()
    {
        DirectoryInfo? current = new(mTempRoot);
        bool valid = Directory.Exists(mTempRoot);
        while(valid && current != null)
        {
            FileAttributes attributes = File.GetAttributes(current.FullName);
            valid = attributes.HasFlag(FileAttributes.Directory)
                    && !attributes.HasFlag(FileAttributes.ReparsePoint);
            current = current.Parent;
        }

        if (!valid)
            throw new InvalidOperationException("The pending-document temp root must have ordinary directory ancestors.");
    }

    private static void ValidateOrdinaryItem(string itemPath)
    {
        FileAttributes attributes = File.GetAttributes(itemPath);
        bool isInvalid = attributes.HasFlag(FileAttributes.Directory)
                         || attributes.HasFlag(FileAttributes.ReparsePoint);
        if (isInvalid)
            throw new InvalidOperationException("A pending-document spool item is not an ordinary file.");
    }

    private static readonly JsonSerializerOptions smJsonOptions = new();

    private sealed class HardBoundedWriteStream : Stream
    {
        internal HardBoundedWriteStream(Stream inner, long maximumBytes, string relativePath)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentException.ThrowIfNullOrEmpty(relativePath);
            if (maximumBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            mInner = inner;
            mMaximumBytes = maximumBytes;
            mRelativePath = relativePath;
        }

        private readonly Stream mInner;
        private readonly long mMaximumBytes;
        private readonly string mRelativePath;

        internal long BytesWritten { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => mInner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            mInner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            EnsureCapacity(count);
            mInner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async Task WriteAsync(byte[] buffer,
                                              int offset,
                                              int count,
                                              CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            EnsureCapacity(count);
            await mInner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
                                                   CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await mInner.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        private void EnsureCapacity(int count)
        {
            if (count > mMaximumBytes - BytesWritten)
            {
                throw new DirectoryIngestionException(
                    DirectoryScanReasonCodes.PendingSpoolLimitExceeded,
                    PendingSpoolLimitExceededDetail,
                    mRelativePath);
            }
        }
    }

    private const int FileBufferSize = 65536;
    private const string OwnedDirectoryPrefix = "saddlerag-pending-documents-";
    private const string PendingSpoolLimitExceededDetail =
        "The pending-document spool exceeds its configured aggregate limit.";
}
