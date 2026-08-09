// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Publishing sink with exact-artifact and prior-extraction reuse.</summary>
internal sealed class DirectoryPublishingSink : IDirectoryScanSink, IDirectoryScanReuseSink, IAsyncDisposable
{
    public DirectoryPublishingSink(ISourceDocumentRepository sourceDocuments,
                                   DirectoryIngestionRequest request,
                                   DirectoryPriorSnapshot prior,
                                   TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sourceDocuments);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mSourceDocuments = sourceDocuments;
        mRequest = request;
        mPrior = prior;
        mTimeProvider = timeProvider;
        mStore = new DirectoryPendingDocumentStore(DirectoryScanLimits.DefaultMaxDocumentCount,
                                                   DirectoryScanLimits.DefaultMaxSpoolBytes);
    }

    private readonly DirectoryPriorSnapshot mPrior;
    private readonly DirectoryIngestionRequest mRequest;
    private readonly ISourceDocumentRepository mSourceDocuments;
    private readonly DirectoryPendingDocumentStore mStore;
    private readonly TimeProvider mTimeProvider;

    internal int DocumentCount => mStore.DocumentCount;

    internal IAsyncEnumerable<PendingDirectoryDocument> ReadDocumentsAsync(CancellationToken ct = default) =>
        mStore.ReadAllAsync(ct);

    public async Task AcceptAsync(DirectoryAcquiredDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<DocChunk> priorChunks = [];
        bool reusedExtraction = false;
        if (mPrior.TryGet(document.Source.NormalizedRelativePath,
                          out PriorDirectoryDocument? prior) &&
            prior != null &&
            CanReuseFreshExtraction(document, prior))
        {
            priorChunks = prior.Chunks;
            reusedExtraction = true;
        }

        PendingDirectoryDocument pending = await PersistAsync(document.Source,
                                                              document.Intake,
                                                              priorChunks,
                                                              reusedExtraction,
                                                              ct);
        await mStore.AddAsync(pending, ct);
        mPrior.Remove(document.Source.NormalizedRelativePath);
    }

    public PreparedDirectoryDocumentReuse? TryPrepareUnchanged(DirectoryStableDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = mPrior.TryGet(document.NormalizedRelativePath,
                                   out PriorDirectoryDocument? prior) &&
                     prior != null &&
                     CanReuseExtraction(document, prior)
            ? new PreparedDirectoryDocumentReuse(document, prior)
            : null;

        return result;
    }

    public async Task AcceptPreparedUnchangedAsync(PreparedDirectoryDocumentReuse prepared,
                                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!mPrior.TryGet(prepared.Document.NormalizedRelativePath,
                           out PriorDirectoryDocument? current) ||
            !ReferenceEquals(current, prepared.Prior))
        {
            throw new InvalidOperationException("The prepared unchanged document is no longer current.");
        }

        byte[] extractionBytes = await ReadExtractionArtifactAsync(prepared.Prior.Revision, ct);
        DocumentIntakeResult intake = RecreateIntake(prepared.Prior, extractionBytes);
        PendingDirectoryDocument pending = await PersistAsync(prepared.Document,
                                                              intake,
                                                              prepared.Prior.Chunks,
                                                              reusedExtraction: true,
                                                              ct);
        await mStore.AddAsync(pending, ct);
        mPrior.Remove(prepared.Document.NormalizedRelativePath);
    }

    public ValueTask DisposeAsync() => mStore.DisposeAsync();

    private async Task<PendingDirectoryDocument> PersistAsync(DirectoryStableDocument stable,
                                                              DocumentIntakeResult intake,
                                                              IReadOnlyList<DocChunk> priorChunks,
                                                              bool reusedExtraction,
                                                              CancellationToken ct)
    {
        DateTime acquiredAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        string documentId = DirectoryPageProducer.MakeSourceDocumentId(mRequest.LibraryId,
                                                                        stable.NormalizedRelativePath);
        var candidate = new SourceDocumentRecord
                            {
                                Id = documentId,
                                LibraryId = mRequest.LibraryId,
                                NormalizedRelativePath = stable.NormalizedRelativePath,
                                DisplayRelativePath = stable.DisplayRelativePath,
                                DisplayName = Path.GetFileName(stable.DisplayRelativePath),
                                SourceUri = $"saddlerag://library/{mRequest.LibraryId}/documents/{documentId}",
                                MediaType = stable.MediaType,
                                FirstSeenVersion = mRequest.Version,
                                CreatedAtUtc = acquiredAtUtc
                            };
        SourceDocumentRecord source = await mSourceDocuments.GetOrCreateDocumentAsync(candidate, ct);
        byte[] originalBytes = stable.Content.ToArray();
        byte[] extractionBytes = intake.ExtractionArtifact.ToArray();
        var revision = new DocumentRevisionRecord
                           {
                               Id = SourceDocumentRepository.MakeRevisionId(mRequest.LibraryId,
                                                                            mRequest.Version,
                                                                            source.Id),
                               DocumentId = source.Id,
                               LibraryId = mRequest.LibraryId,
                               Version = mRequest.Version,
                               ScanRunId = mRequest.ScanRunId,
                               State = DocumentRevisionState.Candidate,
                               SourceModifiedAtUtc = stable.Source.LastWriteTimeUtc,
                               AcquiredAtUtc = acquiredAtUtc,
                               OriginalArtifactHash = DirectoryPageProducer.Hash(originalBytes),
                               OriginalByteLength = originalBytes.LongLength,
                               OriginalMediaType = stable.MediaType,
                               ExtractionArtifactHash = DirectoryPageProducer.Hash(extractionBytes),
                               ExtractionByteLength = extractionBytes.LongLength,
                               ExtractionMediaType = intake.ExtractionMediaType,
                               ExtractionProvenance = intake.Provenance
                           };
        await using var originalStream = new MemoryStream(originalBytes, writable: false);
        await using var extractionStream = new MemoryStream(extractionBytes, writable: false);
        await mSourceDocuments.PersistRevisionAsync(revision, originalStream, extractionStream, ct);
        return new PendingDirectoryDocument(source, revision, intake, priorChunks, reusedExtraction);
    }

    private async Task<byte[]> ReadExtractionArtifactAsync(DocumentRevisionRecord revision,
                                                           CancellationToken ct)
    {
        string extractionHash = revision.ExtractionArtifactHash
                                ?? throw new InvalidDataException("A reusable extraction is missing its artifact hash.");
        await using Stream stream = await mSourceDocuments.OpenArtifactAsync(extractionHash, ct);
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination, ct);
        return destination.ToArray();
    }

    private static bool CanReuseExtraction(DirectoryStableDocument document, PriorDirectoryDocument prior)
    {
        string sourceHash = DirectoryPageProducer.Hash(document.Content.Span);
        DocumentExtractionProvenance? provenance = prior.Revision.ExtractionProvenance;
        DocumentExtractionFingerprint? fingerprint = document.ExtractionFingerprint;
        return prior.Revision.OriginalArtifactHash.Equals(sourceHash, StringComparison.Ordinal)
               && prior.Revision.OriginalMediaType.Equals(document.MediaType, StringComparison.OrdinalIgnoreCase)
               && prior.Revision.ExtractionArtifactHash != null
               && prior.Revision.ExtractionByteLength.HasValue
               && fingerprint != null
               && fingerprint.CanReuseBeforeExtraction
               && MatchesFingerprint(provenance, fingerprint);
    }

    private static bool CanReuseFreshExtraction(DirectoryAcquiredDocument document,
                                                PriorDirectoryDocument prior)
    {
        DocumentExtractionFingerprint? fingerprint = document.Source.ExtractionFingerprint;
        string sourceHash = DirectoryPageProducer.Hash(document.Source.Content.Span);
        string extractionHash = DirectoryPageProducer.Hash(document.Intake.ExtractionArtifact.Span);
        return fingerprint != null
               && prior.Revision.OriginalArtifactHash.Equals(sourceHash, StringComparison.Ordinal)
               && prior.Revision.OriginalMediaType.Equals(document.Source.MediaType,
                                                           StringComparison.OrdinalIgnoreCase)
               && string.Equals(prior.Revision.ExtractionArtifactHash,
                                extractionHash,
                                StringComparison.Ordinal)
               && string.Equals(prior.Revision.ExtractionMediaType,
                                document.Intake.ExtractionMediaType,
                                StringComparison.OrdinalIgnoreCase)
               && MatchesFingerprint(document.Intake.Provenance, fingerprint)
               && MatchesFingerprint(prior.Revision.ExtractionProvenance, fingerprint);
    }

    private static bool MatchesFingerprint(DocumentExtractionProvenance? provenance,
                                           DocumentExtractionFingerprint fingerprint) =>
        provenance != null
        && provenance.ExtractorName.Equals(fingerprint.ExtractorName, StringComparison.Ordinal)
        && provenance.ExtractorVersion.Equals(fingerprint.ExtractorVersion, StringComparison.Ordinal)
        && string.Equals(provenance.ConfigurationHash,
                         fingerprint.ConfigurationHash,
                         StringComparison.Ordinal)
        && provenance.UsedOcr == fingerprint.UsedOcr;

    private static DocumentIntakeResult RecreateIntake(PriorDirectoryDocument prior, byte[] artifact)
    {
        IReadOnlyList<PageRecord> pages = prior.Pages.OrderBy(page => page.Url, StringComparer.Ordinal).ToList();
        string title = pages[0].Title;
        IReadOnlyList<DocumentIntakeSection> sections = pages.Select((page, order) =>
                                                                         new DocumentIntakeSection(
                                                                             order,
                                                                             page.DocumentSource?.Heading ?? page.Title,
                                                                             page.RawContent,
                                                                             page.DocumentSource?.PageStart,
                                                                             page.DocumentSource?.PageEnd))
                                                             .ToList();
        return new DocumentIntakeResult(true,
                                        DocumentIntakeReasonCodes.Extracted,
                                        ReusedExtractionDetail,
                                        title,
                                        sections,
                                        artifact,
                                        prior.Revision.ExtractionMediaType ?? JsonMediaType,
                                        prior.Revision.ExtractionProvenance);
    }

    private const string ReusedExtractionDetail = "The prior compatible extraction was reused.";
    private const string JsonMediaType = "application/json";
}
