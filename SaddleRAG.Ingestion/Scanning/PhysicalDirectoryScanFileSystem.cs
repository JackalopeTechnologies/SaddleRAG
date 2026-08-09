// PhysicalDirectoryScanFileSystem.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Win32.SafeHandles;

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
            using SafeFileHandle handle = PhysicalDirectoryEntryHandle.OpenMetadata(fullPath);
            result = new DirectoryPathResult(PhysicalDirectoryEntryHandle.Snapshot(fullPath, handle),
                                             string.Empty,
                                             null);
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
        catch(PlatformNotSupportedException ex)
        {
            result = FailurePath(DirectoryScanReasonCodes.RootAccessDenied, ex);
        }

        return result;
    }

    public DirectoryEnumerationResult EnumerateDirectory(string fullPath,
                                                          DirectoryEntrySnapshot? expectedSnapshot = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        return new DirectoryEnumerationResult(EnumerateSnapshots(fullPath, expectedSnapshot), string.Empty, null);
    }

    private static IEnumerable<DirectoryEntrySnapshot> EnumerateSnapshots(
        string fullPath,
        DirectoryEntrySnapshot? expectedSnapshot)
    {
        using SafeFileHandle directoryHandle = PhysicalDirectoryEntryHandle.OpenMetadata(fullPath);
        DirectoryEntrySnapshot current = PhysicalDirectoryEntryHandle.Snapshot(fullPath, directoryHandle);
        if (!current.Attributes.HasFlag(FileAttributes.Directory))
            throw new DirectoryNotFoundException("The directory disappeared before it could be enumerated.");
        if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("A linked or redirected directory cannot be enumerated.");
        if (expectedSnapshot != null
            && !PhysicalDirectoryEntryHandle.MatchesExpected(expectedSnapshot, current))
        {
            throw new DirectoryNotFoundException("The directory identity changed before it could be enumerated.");
        }

        string enumerationPath = PhysicalDirectoryEntryHandle.EnumerationPath(fullPath, directoryHandle);
        foreach(string path in Directory.EnumerateFileSystemEntries(enumerationPath))
        {
            string requestedPath = Path.Combine(fullPath, Path.GetFileName(path));
            DirectoryEntrySnapshot snapshot;
            using(SafeFileHandle entryHandle = PhysicalDirectoryEntryHandle.OpenMetadata(path))
                snapshot = PhysicalDirectoryEntryHandle.Snapshot(requestedPath, entryHandle);
            yield return snapshot;
        }
    }

    public Task<StableFileReadResult> ReadStableFileAsync(string fullPath,
                                                          long maxFileBytes,
                                                          CancellationToken cancellationToken = default,
                                                          DirectoryEntrySnapshot? expectedSnapshot = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        if (maxFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "The file-size bound must be positive.");
        return ReadStableFileCoreAsync(fullPath, maxFileBytes, cancellationToken, expectedSnapshot);
    }

    private static async Task<StableFileReadResult> ReadStableFileCoreAsync(string fullPath,
                                                                            long maxFileBytes,
                                                                            CancellationToken cancellationToken,
                                                                            DirectoryEntrySnapshot? expectedSnapshot)
    {
        StableFileReadResult result;
        try
        {
            using SafeFileHandle handle = PhysicalDirectoryEntryHandle.OpenRead(fullPath);
            var before = PhysicalDirectoryEntryHandle.Snapshot(fullPath, handle);
            bool expectedIdentityMatches = expectedSnapshot == null
                                           || PhysicalDirectoryEntryHandle.MatchesExpected(expectedSnapshot, before);
            result = (before.Attributes.HasFlag(FileAttributes.ReparsePoint),
                      expectedIdentityMatches,
                      before.ByteLength > maxFileBytes) switch
                {
                    (true, _, _) => FailureRead(DirectoryScanReasonCodes.FileReparsePointSkipped, before, null),
                    (_, false, _) => FailureRead(DirectoryScanReasonCodes.FileChangedDuringScan, before, null),
                    (_, _, true) => FailureRead(DirectoryScanReasonCodes.FileTooLarge, before, null),
                    _ => await ReadBoundedAsync(fullPath,
                                                handle,
                                                before,
                                                maxFileBytes,
                                                cancellationToken)
                };
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
        catch(PlatformNotSupportedException ex)
        {
            result = FailureRead(DirectoryScanReasonCodes.FileIoError, null, ex);
        }

        return result;
    }

    private static async Task<StableFileReadResult> ReadBoundedAsync(string fullPath,
                                                                     SafeFileHandle handle,
                                                                     DirectoryEntrySnapshot before,
                                                                     long maxFileBytes,
                                                                     CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(handle, FileAccess.Read, BufferSize, handle.IsAsync);
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
            var after = PhysicalDirectoryEntryHandle.Snapshot(fullPath, stream.SafeFileHandle);
            result = new StableFileReadResult(content.ToArray(), before, after, string.Empty, null);
        }

        return result;
    }

    private static DirectoryPathResult FailurePath(string reasonCode, Exception? error) =>
        new(null, reasonCode, error);

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
