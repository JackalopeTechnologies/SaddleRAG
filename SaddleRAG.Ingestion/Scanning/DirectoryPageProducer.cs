// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using System.Text;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>
///     Creates publishing scan sinks and projects normalized document
///     sections into citation-bearing page records.
/// </summary>
public sealed class DirectoryPageProducer
{
    public DirectoryPageProducer(RepositoryFactory repositories, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mRepositories = repositories;
        mTimeProvider = timeProvider;
    }

    private readonly RepositoryFactory mRepositories;
    private readonly TimeProvider mTimeProvider;

    internal async Task<DirectoryPublishingSink> CreateSinkAsync(DirectoryIngestionRequest request,
                                                                 string? previousVersion,
                                                                 CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ISourceDocumentRepository sources = mRepositories.GetSourceDocumentRepository(request.Profile);
        IPageRepository pages = mRepositories.GetPageRepository(request.Profile);
        IChunkRepository chunks = mRepositories.GetChunkRepository(request.Profile);
        DirectoryPriorSnapshot prior = await LoadPriorSnapshotAsync(request.LibraryId,
                                                                    previousVersion,
                                                                    sources,
                                                                    pages,
                                                                    chunks,
                                                                    ct);
        return new DirectoryPublishingSink(sources, request, prior, mTimeProvider);
    }

    internal IReadOnlyList<PageRecord> ProjectPages(PendingDirectoryDocument document,
                                                    SubjectAssignmentRecord assignment)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assignment);
        IReadOnlyList<string> subjectIds = assignment.Secondary
                                                     .Select(selection => selection.SubjectId)
                                                     .Prepend(assignment.Primary.SubjectId)
                                                     .ToArray();
        var result = new List<PageRecord>(document.Intake.Sections.Count);
        foreach(DocumentIntakeSection section in document.Intake.Sections.OrderBy(item => item.Order))
        {
            string heading = string.IsNullOrWhiteSpace(section.Title)
                ? document.Intake.Title
                : section.Title;
            string pageUrl = $"{document.Source.SourceUri}#section-{section.Order:D4}";
            string pageId = MakePageId(document.Revision.LibraryId,
                                       document.Revision.Version,
                                       document.Source.Id,
                                       section.Order);
            var provenance = new DocumentProvenance
                                 {
                                     DocumentId = document.Source.Id,
                                     RevisionId = document.Revision.Id,
                                     SourceUri = document.Source.SourceUri,
                                     RelativePath = document.Source.DisplayRelativePath,
                                     PageStart = section.PageStart,
                                     PageEnd = section.PageEnd,
                                     Heading = heading
                                 };
            result.Add(new PageRecord
                           {
                               Id = pageId,
                               LibraryId = document.Revision.LibraryId,
                               Version = document.Revision.Version,
                               Url = pageUrl,
                               Title = document.Intake.Title,
                               Category = DocCategory.Unclassified,
                               RawContent = section.Content,
                               FetchedAt = document.Revision.AcquiredAtUtc,
                               ContentHash = Hash(Encoding.UTF8.GetBytes(section.Content)),
                               Depth = 0,
                               ParentUrl = null,
                               DocumentSource = provenance,
                               SubjectIds = subjectIds,
                               SubjectTaxonomyVersion = assignment.TaxonomyVersion
                           });
        }

        return result;
    }

    private static async Task<DirectoryPriorSnapshot> LoadPriorSnapshotAsync(
        string libraryId,
        string? previousVersion,
        ISourceDocumentRepository sources,
        IPageRepository pages,
        IChunkRepository chunks,
        CancellationToken ct)
    {
        DirectoryPriorSnapshot result;
        if (string.IsNullOrWhiteSpace(previousVersion))
            result = DirectoryPriorSnapshot.Empty;
        else
        {
            IReadOnlyList<DocumentRevisionRecord> revisions = await sources.GetRevisionsAsync(libraryId,
                                                                                               previousVersion,
                                                                                               ct);
            IReadOnlyList<PageRecord> priorPages = await pages.GetPagesAsync(libraryId, previousVersion, ct);
            IReadOnlyList<DocChunk> priorChunks = await chunks.GetChunksAsync(libraryId, previousVersion, ct);
            var pagesByRevision = priorPages.Where(page => page.DocumentSource != null)
                                            .GroupBy(page => page.DocumentSource?.RevisionId ?? string.Empty,
                                                     StringComparer.Ordinal)
                                            .ToDictionary(group => group.Key,
                                                          group => (IReadOnlyList<PageRecord>)group.ToList(),
                                                          StringComparer.Ordinal);
            var chunksByRevision = priorChunks.Where(chunk => chunk.DocumentSource != null)
                                              .GroupBy(chunk => chunk.DocumentSource?.RevisionId ?? string.Empty,
                                                       StringComparer.Ordinal)
                                              .ToDictionary(group => group.Key,
                                                            group => (IReadOnlyList<DocChunk>)group.ToList(),
                                                            StringComparer.Ordinal);
            var documents = new Dictionary<string, PriorDirectoryDocument>(StringComparer.OrdinalIgnoreCase);
            foreach(DocumentRevisionRecord revision in revisions)
            {
                if (revision.State == DocumentRevisionState.Published
                    && pagesByRevision.TryGetValue(revision.Id,
                                                   out IReadOnlyList<PageRecord>? documentPages)
                    && documentPages is { Count: > 0 })
                {
                    AddPriorDocument(revision,
                                     documentPages,
                                     chunksByRevision,
                                     documents);
                }
            }

            result = new DirectoryPriorSnapshot(documents);
        }

        return result;
    }

    private static void AddPriorDocument(
        DocumentRevisionRecord revision,
        IReadOnlyList<PageRecord> documentPages,
        IReadOnlyDictionary<string, IReadOnlyList<DocChunk>> chunksByRevision,
        Dictionary<string, PriorDirectoryDocument> documents)
    {
        DocumentProvenance? source = documentPages[0].DocumentSource;
        if (source != null)
        {
            string normalizedPath = NormalizeRelativePath(source.RelativePath);
            chunksByRevision.TryGetValue(revision.Id, out IReadOnlyList<DocChunk>? documentChunks);
            documents[normalizedPath] = new PriorDirectoryDocument(revision,
                                                                   documentPages,
                                                                   documentChunks ?? []);
        }
    }

    internal static string MakeSourceDocumentId(string libraryId, string normalizedRelativePath)
    {
        string identity = string.Join(UnitSeparator, libraryId, normalizedRelativePath);
        return $"source-document-{Hash(Encoding.UTF8.GetBytes(identity))}";
    }

    private static string MakePageId(string libraryId, string version, string documentId, int order)
    {
        string identity = string.Join(UnitSeparator,
                                      libraryId,
                                      version,
                                      documentId,
                                      order.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return $"document-page-{Hash(Encoding.UTF8.GetBytes(identity))}";
    }

    internal static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/')
                    .Normalize(NormalizationForm.FormC)
                    .ToLowerInvariant();

    private const char UnitSeparator = '\u001f';
}
