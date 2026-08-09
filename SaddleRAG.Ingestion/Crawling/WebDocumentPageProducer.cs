// WebDocumentPageProducer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Feeds an acquired web PDF or DOCX through the shared document-intake
///     service and projects its sections into the crawler's PageRecord stream.
/// </summary>
public sealed class WebDocumentPageProducer
{
    public WebDocumentPageProducer(ISourceDocumentRepository sourceDocuments,
                                   IDocumentIntake documentIntake,
                                   TimeProvider timeProvider,
                                   ILogger<WebDocumentPageProducer> logger,
                                   RepositoryFactory? repositoryFactory = null)
    {
        ArgumentNullException.ThrowIfNull(sourceDocuments);
        ArgumentNullException.ThrowIfNull(documentIntake);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        mSourceDocuments = sourceDocuments;
        mDocumentIntake = documentIntake;
        mTimeProvider = timeProvider;
        mLogger = logger;
        mRepositoryFactory = repositoryFactory;
    }

    private readonly IDocumentIntake mDocumentIntake;
    private readonly ILogger<WebDocumentPageProducer> mLogger;
    private readonly ISourceDocumentRepository mSourceDocuments;
    private readonly TimeProvider mTimeProvider;
    private readonly RepositoryFactory? mRepositoryFactory;

    public async Task<IReadOnlyList<PageRecord>> ProduceAsync(ScrapeJob job,
                                                              string scanRunId,
                                                              FetchedWebResponse response,
                                                              DocumentResponseKind kind,
                                                              int depth,
                                                              string? parentUrl,
                                                              IngestionPersistenceMode persistMode,
                                                              CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(job.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(job.Version);
        ValidateValueArguments(kind, depth, persistMode);
        ct.ThrowIfCancellationRequested();

        string canonicalOriginalUrl = CanonicalizeOriginalUrl(response.OriginalUrl);
        string mediaType = MediaTypeFor(kind);
        string fileName = FileNameFor(response.FinalUrl, kind);
        byte[] originalBytes = response.Body.ToArray();
        var request = new DocumentIntakeRequest(fileName,
                                                canonicalOriginalUrl,
                                                mediaType,
                                                originalBytes);
        DocumentIntakeResult intake = await mDocumentIntake.ReadAsync(request, ct);
        EnsureSuccessfulIntake(intake, response.OriginalUrl);

        DateTime acquiredAtUtc = mTimeProvider.GetUtcNow().UtcDateTime;
        string documentId = MakeSourceDocumentId(job.LibraryId, canonicalOriginalUrl);
        var candidate = new SourceDocumentRecord
                            {
                                Id = documentId,
                                LibraryId = job.LibraryId,
                                NormalizedRelativePath = canonicalOriginalUrl,
                                DisplayRelativePath = response.OriginalUrl,
                                DisplayName = fileName,
                                SourceUri = response.OriginalUrl,
                                MediaType = mediaType,
                                FirstSeenVersion = job.Version,
                                LastSeenVersion = job.Version,
                                CreatedAtUtc = acquiredAtUtc,
                                UpdatedAtUtc = acquiredAtUtc
                            };
        ISourceDocumentRepository sourceDocuments = ResolveSourceDocuments(job.DatabaseProfile);
        SourceDocumentRecord source = candidate;
        if (persistMode == IngestionPersistenceMode.Full)
            source = await sourceDocuments.GetOrCreateDocumentAsync(candidate, ct);

        string revisionId = SourceDocumentRepository.MakeRevisionId(job.LibraryId,
                                                                      job.Version,
                                                                      source.Id);
        byte[] extractionBytes = intake.ExtractionArtifact.ToArray();
        DateTime? sourceLastModifiedAtUtc = ReadLastModified(response.Headers);
        var revision = new DocumentRevisionRecord
                           {
                               Id = revisionId,
                               DocumentId = source.Id,
                               LibraryId = job.LibraryId,
                               Version = job.Version,
                               ScanRunId = scanRunId,
                               State = DocumentRevisionState.Candidate,
                               SourceModifiedAtUtc = null,
                               AcquiredAtUtc = acquiredAtUtc,
                               OriginalArtifactHash = Hash(originalBytes),
                               OriginalByteLength = originalBytes.LongLength,
                               OriginalMediaType = mediaType,
                               ExtractionArtifactHash = Hash(extractionBytes),
                               ExtractionByteLength = extractionBytes.LongLength,
                               ExtractionMediaType = intake.ExtractionMediaType,
                               ExtractionProvenance = intake.Provenance,
                               OriginalUrl = response.OriginalUrl,
                               AttemptedUrl = response.AttemptedUrl,
                               FinalUrl = response.FinalUrl,
                               SourceETag = HeaderValue(response.Headers, ETagHeader),
                               SourceLastModifiedAtUtc = sourceLastModifiedAtUtc
                           };

        if (persistMode == IngestionPersistenceMode.Full)
            await PersistRevisionAsync(sourceDocuments, revision, originalBytes, extractionBytes, ct);

        IReadOnlyList<PageRecord> result = ProjectPages(job,
                                                        source,
                                                        revision,
                                                        intake,
                                                        response,
                                                        depth,
                                                        parentUrl);
        mLogger.LogInformation("Produced {Count} pages from web document {Url}", result.Count, response.OriginalUrl);
        return result;
    }

    private async Task PersistRevisionAsync(ISourceDocumentRepository sourceDocuments,
                                            DocumentRevisionRecord revision,
                                            byte[] originalBytes,
                                            byte[] extractionBytes,
                                            CancellationToken ct)
    {
        await using var originalStream = new MemoryStream(originalBytes, writable: false);
        await using var extractionStream = new MemoryStream(extractionBytes, writable: false);
        await sourceDocuments.PersistRevisionAsync(revision, originalStream, extractionStream, ct);
    }

    private ISourceDocumentRepository ResolveSourceDocuments(string? profile)
    {
        ISourceDocumentRepository result = string.IsNullOrEmpty(profile) || mRepositoryFactory == null
                                               ? mSourceDocuments
                                               : mRepositoryFactory.GetSourceDocumentRepository(profile);
        return result;
    }

    private static IReadOnlyList<PageRecord> ProjectPages(ScrapeJob job,
                                                           SourceDocumentRecord source,
                                                           DocumentRevisionRecord revision,
                                                           DocumentIntakeResult intake,
                                                           FetchedWebResponse response,
                                                           int depth,
                                                           string? parentUrl)
    {
        var result = new List<PageRecord>(intake.Sections.Count);
        foreach(DocumentIntakeSection section in intake.Sections.OrderBy(item => item.Order))
        {
            string heading = string.IsNullOrWhiteSpace(section.Title) ? intake.Title : section.Title;
            string pageUrl = $"{response.FinalUrl}#section-{section.Order:D4}";
            var provenance = new DocumentProvenance
                                 {
                                     DocumentId = source.Id,
                                     RevisionId = revision.Id,
                                     SourceUri = response.OriginalUrl,
                                     RelativePath = response.OriginalUrl,
                                     PageStart = section.PageStart,
                                     PageEnd = section.PageEnd,
                                     Heading = heading,
                                     OriginalUrl = response.OriginalUrl,
                                     AttemptedUrl = response.AttemptedUrl,
                                     FinalUrl = response.FinalUrl
                                 };
            result.Add(new PageRecord
                           {
                               Id = MakePageId(job.LibraryId, job.Version, source.Id, section.Order),
                               LibraryId = job.LibraryId,
                               Version = job.Version,
                               Url = pageUrl,
                               Title = intake.Title,
                               Category = DocCategory.Unclassified,
                               RawContent = section.Content,
                               FetchedAt = revision.AcquiredAtUtc,
                               ContentHash = Hash(Encoding.UTF8.GetBytes(section.Content)),
                               Depth = depth,
                               ParentUrl = parentUrl,
                               DocumentSource = provenance
                           });
        }

        return result;
    }

    private static void ValidateValueArguments(DocumentResponseKind kind,
                                               int depth,
                                               IngestionPersistenceMode persistMode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(depth);
        if (kind is not DocumentResponseKind.Pdf and not DocumentResponseKind.Docx)
        {
            throw new ArgumentOutOfRangeException(nameof(kind),
                                                  kind,
                                                  "Only positively identified PDF and DOCX responses can enter document intake.");
        }

        if (!Enum.IsDefined(typeof(IngestionPersistenceMode), persistMode))
            throw new ArgumentOutOfRangeException(nameof(persistMode), persistMode, "Unknown persistence mode.");
    }

    private static void EnsureSuccessfulIntake(DocumentIntakeResult intake, string originalUrl)
    {
        ArgumentNullException.ThrowIfNull(intake);
        if (!intake.Succeeded)
        {
            string reasonCode = string.IsNullOrWhiteSpace(intake.ReasonCode)
                ? DocumentIntakeReasonCodes.ExtractionFailed
                : intake.ReasonCode;
            string detail = string.IsNullOrWhiteSpace(intake.Detail)
                ? $"The supported document at '{originalUrl}' could not be extracted."
                : intake.Detail;
            throw new SupportedDocumentIngestionException(reasonCode, detail);
        }

        if (intake.Sections.Count == 0)
        {
            throw new SupportedDocumentIngestionException(
                DocumentIntakeReasonCodes.EmptyContent,
                $"The supported document at '{originalUrl}' produced no searchable sections.");
        }
    }

    private static string CanonicalizeOriginalUrl(string originalUrl)
    {
        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("The original document URL must be absolute.", nameof(originalUrl));

        string result = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                                          UriFormat.UriEscaped);
        return result;
    }

    private static string FileNameFor(string finalUrl, DocumentResponseKind kind)
    {
        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("The final document URL must be absolute.", nameof(finalUrl));

        string extension = kind == DocumentResponseKind.Pdf ? PdfExtension : DocxExtension;
        string result = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(result))
            result = DefaultFileNamePrefix;
        if (!Path.GetExtension(result).Equals(extension, StringComparison.OrdinalIgnoreCase))
            result += extension;
        return result;
    }

    private static string MediaTypeFor(DocumentResponseKind kind) =>
        kind == DocumentResponseKind.Pdf ? PdfMediaType : DocxMediaType;

    private static DateTime? ReadLastModified(IReadOnlyDictionary<string, string> headers)
    {
        DateTime? result = null;
        string? value = HeaderValue(headers, LastModifiedHeader);
        if (!string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value,
                                       CultureInfo.InvariantCulture,
                                       DateTimeStyles.AllowWhiteSpaces
                                       | DateTimeStyles.AssumeUniversal
                                       | DateTimeStyles.AdjustToUniversal,
                                       out DateTimeOffset parsed))
        {
            result = parsed.UtcDateTime;
        }

        return result;
    }

    private static string? HeaderValue(IReadOnlyDictionary<string, string> headers, string name)
    {
        headers.TryGetValue(name, out string? result);
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string MakeSourceDocumentId(string libraryId, string canonicalOriginalUrl)
    {
        string identity = string.Join(UnitSeparator, libraryId, canonicalOriginalUrl);
        return $"source-document-{Hash(Encoding.UTF8.GetBytes(identity))}";
    }

    private static string MakePageId(string libraryId, string version, string documentId, int order)
    {
        string identity = string.Join(UnitSeparator,
                                      libraryId,
                                      version,
                                      documentId,
                                      order.ToString(CultureInfo.InvariantCulture));
        return $"document-page-{Hash(Encoding.UTF8.GetBytes(identity))}";
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private const string DefaultFileNamePrefix = "document";
    private const string DocxExtension = ".docx";
    private const string DocxMediaType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string ETagHeader = "etag";
    private const string LastModifiedHeader = "last-modified";
    private const string PdfExtension = ".pdf";
    private const string PdfMediaType = "application/pdf";
    private const char UnitSeparator = '\u001f';
}
