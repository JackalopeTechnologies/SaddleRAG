// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using System.Text;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Persists one scan into an ephemeral preview-only workspace.</summary>
internal sealed class DirectoryPreviewSink : IDirectoryScanSink
{
    public DirectoryPreviewSink(ISourceDocumentRepository sourceDocuments,
                                string workspaceLibraryId,
                                string scanRunId,
                                TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sourceDocuments);
        ArgumentException.ThrowIfNullOrEmpty(workspaceLibraryId);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mSourceDocuments = sourceDocuments;
        mWorkspaceLibraryId = workspaceLibraryId;
        mScanRunId = scanRunId;
        mTimeProvider = timeProvider;
    }

    private readonly string mScanRunId;
    private readonly ISourceDocumentRepository mSourceDocuments;
    private readonly TimeProvider mTimeProvider;
    private readonly string mWorkspaceLibraryId;

    public async Task AcceptAsync(DirectoryAcquiredDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        DirectoryStableDocument source = document.Source;
        string documentId = MakeDocumentId(mWorkspaceLibraryId, source.NormalizedRelativePath);
        DateTime acquiredAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        var candidate = new SourceDocumentRecord
                            {
                                Id = documentId,
                                LibraryId = mWorkspaceLibraryId,
                                NormalizedRelativePath = source.NormalizedRelativePath,
                                DisplayRelativePath = source.DisplayRelativePath,
                                DisplayName = Path.GetFileName(source.DisplayRelativePath),
                                SourceUri = $"saddlerag://library/{mWorkspaceLibraryId}/documents/{documentId}",
                                MediaType = source.MediaType,
                                FirstSeenVersion = PreviewVersion,
                                LastSeenVersion = PreviewVersion,
                                CreatedAtUtc = acquiredAtUtc,
                                UpdatedAtUtc = acquiredAtUtc
                            };
        SourceDocumentRecord stored = await mSourceDocuments.GetOrCreateDocumentAsync(candidate, ct);
        byte[] originalBytes = source.Content.ToArray();
        byte[] extractionBytes = document.Intake.ExtractionArtifact.ToArray();
        var revision = new DocumentRevisionRecord
                           {
                               Id = SourceDocumentRepository.MakeRevisionId(mWorkspaceLibraryId,
                                                                            PreviewVersion,
                                                                            stored.Id),
                               DocumentId = stored.Id,
                               LibraryId = mWorkspaceLibraryId,
                               Version = PreviewVersion,
                               ScanRunId = mScanRunId,
                               State = DocumentRevisionState.Candidate,
                               SourceModifiedAtUtc = source.Source.LastWriteTimeUtc,
                               AcquiredAtUtc = acquiredAtUtc,
                               OriginalArtifactHash = Hash(originalBytes),
                               OriginalByteLength = originalBytes.LongLength,
                               OriginalMediaType = source.MediaType,
                               ExtractionArtifactHash = Hash(extractionBytes),
                               ExtractionByteLength = extractionBytes.LongLength,
                               ExtractionMediaType = document.Intake.ExtractionMediaType,
                               ExtractionProvenance = document.Intake.Provenance
                           };
        await using var originalStream = new MemoryStream(originalBytes, writable: false);
        await using var extractionStream = new MemoryStream(extractionBytes, writable: false);
        await mSourceDocuments.PersistRevisionAsync(revision, originalStream, extractionStream, ct);
    }

    private static string MakeDocumentId(string workspaceLibraryId, string relativePath)
    {
        string identity = string.Join(UnitSeparator, workspaceLibraryId, relativePath);
        return $"source-document-{Hash(Encoding.UTF8.GetBytes(identity))}";
    }

    private static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private const string PreviewVersion = "preview";
    private const char UnitSeparator = '\u001f';
}
