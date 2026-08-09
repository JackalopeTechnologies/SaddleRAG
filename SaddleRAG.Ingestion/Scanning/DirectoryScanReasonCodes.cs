// DirectoryScanReasonCodes.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Stable reason codes emitted by explicit directory previews.</summary>
public static class DirectoryScanReasonCodes
{
    public const string ScanCompleted = "SCAN_COMPLETED";
    public const string ScanCompletedWithErrors = "SCAN_COMPLETED_WITH_ERRORS";
    public const string ScanFailed = "SCAN_FAILED";
    public const string LibraryNotRegistered = "LIBRARY_NOT_REGISTERED";
    public const string LibraryNotBound = "LIBRARY_NOT_BOUND";
    public const string RootPathRequired = "ROOT_PATH_REQUIRED";
    public const string RootPathNotAbsolute = "ROOT_PATH_NOT_ABSOLUTE";
    public const string RootNotFound = "ROOT_NOT_FOUND";
    public const string RootNotDirectory = "ROOT_NOT_DIRECTORY";
    public const string RootAccessDenied = "ROOT_ACCESS_DENIED";
    public const string RootReparsePointNotAllowed = "ROOT_REPARSE_POINT_NOT_ALLOWED";
    public const string FileExtracted = "FILE_EXTRACTED";
    public const string FileUnsupportedType = "FILE_UNSUPPORTED_TYPE";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string FileEmpty = "FILE_EMPTY";
    public const string FileAccessDenied = "FILE_ACCESS_DENIED";
    public const string FileLocked = "FILE_LOCKED";
    public const string FileIoError = "FILE_IO_ERROR";
    public const string FileDisappeared = "FILE_DISAPPEARED";
    public const string FileChangedDuringScan = "FILE_CHANGED_DURING_SCAN";
    public const string FileReparsePointSkipped = "FILE_REPARSE_POINT_SKIPPED";
    public const string DirectoryAccessDenied = "DIRECTORY_ACCESS_DENIED";
    public const string DirectoryIoError = "DIRECTORY_IO_ERROR";
    public const string DirectoryDisappeared = "DIRECTORY_DISAPPEARED";
    public const string DirectoryReparsePointSkipped = "DIRECTORY_REPARSE_POINT_SKIPPED";
    public const string PathOutsideRoot = "PATH_OUTSIDE_ROOT";
    public const string PathIdentityCollision = "PATH_IDENTITY_COLLISION";
    public const string DirectoryCountLimitExceeded = "DIRECTORY_COUNT_LIMIT_EXCEEDED";
    public const string EntryCountLimitExceeded = "ENTRY_COUNT_LIMIT_EXCEEDED";
    public const string DocumentCountLimitExceeded = "DOCUMENT_COUNT_LIMIT_EXCEEDED";
    public const string TotalBytesLimitExceeded = "TOTAL_BYTES_LIMIT_EXCEEDED";
    public const string SectionCountLimitExceeded = "SECTION_COUNT_LIMIT_EXCEEDED";
    public const string ChunkCountLimitExceeded = "CHUNK_COUNT_LIMIT_EXCEEDED";
    public const string PendingSpoolLimitExceeded = "PENDING_SPOOL_LIMIT_EXCEEDED";
    public const string DocumentPersistenceFailed = "DOCUMENT_PERSISTENCE_FAILED";
}
