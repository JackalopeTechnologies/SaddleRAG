// PhysicalDirectoryScanFileSystem.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Physical filesystem adapter for an explicitly requested preview.</summary>
public sealed class PhysicalDirectoryScanFileSystem : IDirectoryScanFileSystem
{
    public DirectoryPathResult InspectPath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        DirectoryPathResult result;
        try
        {
            result = File.Exists(fullPath) || Directory.Exists(fullPath)
                ? new DirectoryPathResult(CreateSnapshot(fullPath), string.Empty, null)
                : FailurePath(DirectoryScanReasonCodes.RootNotFound, null);
        }
        catch(UnauthorizedAccessException ex)
        {
            result = FailurePath(DirectoryScanReasonCodes.RootAccessDenied, ex);
        }
        catch(System.Security.SecurityException ex)
        {
            result = FailurePath(DirectoryScanReasonCodes.RootAccessDenied, ex);
        }
        catch(IOException ex)
        {
            result = FailurePath(DirectoryScanReasonCodes.RootNotFound, ex);
        }

        return result;
    }

    public DirectoryEnumerationResult EnumerateDirectory(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        DirectoryEnumerationResult result;
        try
        {
            var paths = Directory.GetFileSystemEntries(fullPath);
            var entries = paths.Select(CreateSnapshot).ToArray();
            result = new DirectoryEnumerationResult(entries, string.Empty, null);
        }
        catch(UnauthorizedAccessException ex)
        {
            result = FailureEnumeration(DirectoryScanReasonCodes.DirectoryAccessDenied, ex);
        }
        catch(System.Security.SecurityException ex)
        {
            result = FailureEnumeration(DirectoryScanReasonCodes.DirectoryAccessDenied, ex);
        }
        catch(DirectoryNotFoundException ex)
        {
            result = FailureEnumeration(DirectoryScanReasonCodes.DirectoryDisappeared, ex);
        }
        catch(IOException ex)
        {
            result = FailureEnumeration(DirectoryScanReasonCodes.DirectoryIoError, ex);
        }

        return result;
    }

    public Task<StableFileReadResult> ReadStableFileAsync(string fullPath,
                                                          long maxFileBytes,
                                                          CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        if (maxFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "The file-size bound must be positive.");
        return ReadStableFileCoreAsync(fullPath, maxFileBytes, cancellationToken);
    }

    private static async Task<StableFileReadResult> ReadStableFileCoreAsync(string fullPath,
                                                                            long maxFileBytes,
                                                                            CancellationToken cancellationToken)
    {
        StableFileReadResult result;
        try
        {
            var before = CreateSnapshot(fullPath);
            if (before.ByteLength > maxFileBytes)
                result = FailureRead(DirectoryScanReasonCodes.FileTooLarge, before, null);
            else
            {
                result = before.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? FailureRead(DirectoryScanReasonCodes.FileReparsePointSkipped, before, null)
                    : await ReadBoundedAsync(fullPath, before, maxFileBytes, cancellationToken);
            }
        }
        catch(UnauthorizedAccessException ex)
        {
            result = FailureRead(DirectoryScanReasonCodes.FileAccessDenied, null, ex);
        }
        catch(System.Security.SecurityException ex)
        {
            result = FailureRead(DirectoryScanReasonCodes.FileAccessDenied, null, ex);
        }
        catch(FileNotFoundException ex)
        {
            result = FailureRead(DirectoryScanReasonCodes.FileDisappeared, null, ex);
        }
        catch(DirectoryNotFoundException ex)
        {
            result = FailureRead(DirectoryScanReasonCodes.FileDisappeared, null, ex);
        }
        catch(IOException ex)
        {
            var reasonCode = IsSharingViolation(ex)
                ? DirectoryScanReasonCodes.FileLocked
                : DirectoryScanReasonCodes.FileIoError;
            result = FailureRead(reasonCode, null, ex);
        }

        return result;
    }

    private static async Task<StableFileReadResult> ReadBoundedAsync(string fullPath,
                                                                     DirectoryEntrySnapshot before,
                                                                     long maxFileBytes,
                                                                     CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(fullPath,
                                                FileMode.Open,
                                                FileAccess.Read,
                                                FileShare.Read | FileShare.Delete,
                                                BufferSize,
                                                FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new MemoryStream(capacity: (int)Math.Min(before.ByteLength, BufferSize));
        var buffer = new byte[BufferSize];
        var exceedsLimit = false;
        var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
        while (bytesRead > 0 && !exceedsLimit)
        {
            exceedsLimit = content.Length + bytesRead > maxFileBytes;
            if (!exceedsLimit)
                await content.WriteAsync(buffer.AsMemory(start: 0, bytesRead), cancellationToken);
            if (!exceedsLimit)
                bytesRead = await stream.ReadAsync(buffer, cancellationToken);
        }

        StableFileReadResult result;
        if (exceedsLimit)
            result = FailureRead(DirectoryScanReasonCodes.FileTooLarge, before, null);
        else
        {
            var after = CreateSnapshot(fullPath);
            result = new StableFileReadResult(content.ToArray(), before, after, string.Empty, null);
        }

        return result;
    }

    private static DirectoryEntrySnapshot CreateSnapshot(string fullPath)
    {
        var attributes = File.GetAttributes(fullPath);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        var byteLength = isDirectory ? 0 : new FileInfo(fullPath).Length;
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
        return new DirectoryEntrySnapshot(Path.GetFullPath(fullPath), attributes, byteLength, lastWriteTimeUtc);
    }

    private static DirectoryPathResult FailurePath(string reasonCode, Exception? error) =>
        new(null, reasonCode, error);

    private static DirectoryEnumerationResult FailureEnumeration(string reasonCode, Exception error) =>
        new([], reasonCode, error);

    private static StableFileReadResult FailureRead(string reasonCode,
                                                    DirectoryEntrySnapshot? before,
                                                    Exception? error) =>
        new(ReadOnlyMemory<byte>.Empty, before, null, reasonCode, error);

    private static bool IsSharingViolation(IOException exception)
    {
        var nativeCode = exception.HResult & NativeErrorMask;
        return nativeCode is SharingViolationError or LockViolationError;
    }

    private const int BufferSize = 81920;
    private const int NativeErrorMask = 0xffff;
    private const int SharingViolationError = 32;
    private const int LockViolationError = 33;
}
