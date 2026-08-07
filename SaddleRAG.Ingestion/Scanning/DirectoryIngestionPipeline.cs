// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>
///     Builds one complete manual-directory candidate. Publication of its
///     revision state and library pointer remains the coordinator's boundary.
/// </summary>
public sealed class DirectoryIngestionPipeline : IDirectoryIngestionPipeline
{
    public DirectoryIngestionPipeline(DirectoryScanEngine scanEngine,
                                      DirectoryPageProducer pageProducer,
                                      RepositoryFactory repositories,
                                      SubjectDescriptorBuilder descriptorBuilder,
                                      SubjectCatalogBuilder catalogBuilder,
                                      ISubjectClassifier subjectClassifier,
                                      IngestionPageProcessor pageProcessor,
                                      ILogger<DirectoryIngestionPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(scanEngine);
        ArgumentNullException.ThrowIfNull(pageProducer);
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(descriptorBuilder);
        ArgumentNullException.ThrowIfNull(catalogBuilder);
        ArgumentNullException.ThrowIfNull(subjectClassifier);
        ArgumentNullException.ThrowIfNull(pageProcessor);
        ArgumentNullException.ThrowIfNull(logger);
        mScanEngine = scanEngine;
        mPageProducer = pageProducer;
        mRepositories = repositories;
        mDescriptorBuilder = descriptorBuilder;
        mCatalogBuilder = catalogBuilder;
        mSubjectClassifier = subjectClassifier;
        mPageProcessor = pageProcessor;
    }

    private readonly SubjectCatalogBuilder mCatalogBuilder;
    private readonly SubjectDescriptorBuilder mDescriptorBuilder;
    private readonly DirectoryPageProducer mPageProducer;
    private readonly IngestionPageProcessor mPageProcessor;
    private readonly RepositoryFactory mRepositories;
    private readonly DirectoryScanEngine mScanEngine;
    private readonly ISubjectClassifier mSubjectClassifier;

    public async Task<DirectoryIngestionPipelineResult> ExecuteAsync(
        DirectoryIngestionRequest request,
        Action<DirectoryScanProgress>? onProgress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        ILibraryRepository libraries = mRepositories.GetLibraryRepository(request.Profile);
        DirectoryLibraryDefinition definition = request.Definition;
        LibraryRecord? library = await libraries.GetLibraryAsync(request.LibraryId, ct);
        string? previousVersion = PublishedCurrentVersion(library);
        LibraryVersionRecord? priorManifest = previousVersion == null
            ? null
            : await libraries.GetVersionAsync(request.LibraryId, previousVersion, ct);
        DirectoryPublishingSink sink = await mPageProducer.CreateSinkAsync(request, previousVersion, ct);
        var scanRequest = new DirectoryScanRequest
                              {
                                  LibraryId = request.LibraryId,
                                  ScanRunId = request.ScanRunId,
                                  RootPath = definition.RootPath,
                                  Recursive = definition.Recursive,
                                  AllowedExtensions = definition.AllowedExtensions,
                                  ExclusionPatterns = definition.ExclusionPatterns,
                                  MaxFileBytes = DirectoryScanLimits.DefaultMaxFileBytes
                              };

        DirectoryScanReport report = await mScanEngine.ScanAsync(scanRequest, sink, onProgress, ct);
        EnsureComplete(report);
        string libraryHint = string.IsNullOrWhiteSpace(library?.Hint) ? request.LibraryId : library.Hint;
        PipelineDocuments processed = await ProcessDocumentsAsync(request,
                                                                  sink.Documents,
                                                                  priorManifest,
                                                                  libraryHint,
                                                                  ct);
        await PersistIndexesAsync(request, processed.Chunks, ct);
        return new DirectoryIngestionPipelineResult(sink.Documents.Count,
                                                    processed.Pages.Count,
                                                    processed.Chunks.Count,
                                                    mPageProcessor.EmbeddingProviderId,
                                                    mPageProcessor.EmbeddingModelName,
                                                    mPageProcessor.EmbeddingDimensions,
                                                    mPageProcessor.ClassifierBackend,
                                                    mPageProcessor.ClassifierModel,
                                                    processed.TaxonomyVersion);
    }

    private async Task<PipelineDocuments> ProcessDocumentsAsync(
        DirectoryIngestionRequest request,
        IReadOnlyList<PendingDirectoryDocument> documents,
        LibraryVersionRecord? priorManifest,
        string libraryHint,
        CancellationToken ct)
    {
        PipelineDocuments result;
        if (documents.Count == 0)
            result = new PipelineDocuments([], [], TaxonomyVersion: null);
        else
        {
            IReadOnlyList<SubjectDescriptor> descriptors = documents.Select(document =>
                                                                                  mDescriptorBuilder.Build(
                                                                                      document.Source,
                                                                                      document.Revision,
                                                                                      document.Intake))
                                                                          .ToList();
            ISubjectCatalogRepository catalogRepository =
                mRepositories.GetSubjectCatalogRepository(request.Profile);
            ISubjectAssignmentRepository assignmentRepository =
                mRepositories.GetSubjectAssignmentRepository(request.Profile);
            SubjectCatalogRecord catalog = await mCatalogBuilder.ReconcileAsync(catalogRepository,
                                                                                 request.LibraryId,
                                                                                 request.ScanRunId,
                                                                                 descriptors,
                                                                                 ct);
            var pages = new List<PageRecord>();
            var chunks = new List<DocChunk>();
            IPageRepository pageRepository = mRepositories.GetPageRepository(request.Profile);
            IChunkRepository chunkRepository = mRepositories.GetChunkRepository(request.Profile);
            bool reuseEmbeddings = IsCompatibleProcessingManifest(priorManifest);
            for(var index = 0; index < documents.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                PendingDirectoryDocument document = documents[index];
                SubjectAssignmentRecord assignment = await mSubjectClassifier.ClassifyAsync(assignmentRepository,
                                                                                             descriptors[index],
                                                                                             catalog,
                                                                                             request.Version,
                                                                                             request.ScanRunId,
                                                                                             ct);
                IReadOnlyList<PageRecord> projected = mPageProducer.ProjectPages(document, assignment);
                Func<IReadOnlyList<DocChunk>, IReadOnlyList<DocChunk>>? reuse =
                    reuseEmbeddings && document.ReusedExtraction
                        ? generated => TryReuseEmbeddings(generated, document.PriorChunks) ?? generated
                        : null;
                IngestionPageBatchResult batch = await mPageProcessor.ProcessPagesAsync(
                                                      projected,
                                                      libraryHint,
                                                      pageRepository,
                                                      chunkRepository,
                                                      PagePersistenceIntent.UpsertAlways,
                                                      reuse,
                                                      ct);
                pages.AddRange(batch.Pages);
                chunks.AddRange(batch.Chunks);
            }

            result = new PipelineDocuments(pages, chunks, catalog.TaxonomyVersion);
        }

        return result;
    }

    private static IReadOnlyList<DocChunk>? TryReuseEmbeddings(IReadOnlyList<DocChunk> generated,
                                                               IReadOnlyList<DocChunk> prior)
    {
        IReadOnlyList<DocChunk> orderedCurrent = OrderChunks(generated);
        IReadOnlyList<DocChunk> orderedPrior = OrderChunks(prior);
        IReadOnlyList<DocChunk>? result = null;
        if (orderedCurrent.Count == orderedPrior.Count &&
            orderedPrior.All(chunk => chunk.Embedding != null) &&
            orderedCurrent.Zip(orderedPrior).All(pair => SameEmbeddingInput(pair.First, pair.Second)))
        {
            result = orderedCurrent.Select((chunk, index) =>
                                               chunk with { Embedding = orderedPrior[index].Embedding?.ToArray() })
                                   .ToList();
        }

        return result;
    }

    private async Task PersistIndexesAsync(DirectoryIngestionRequest request,
                                           IReadOnlyList<DocChunk> chunks,
                                           CancellationToken ct)
    {
        IBm25ShardRepository shards = mRepositories.GetBm25ShardRepository(request.Profile);
        ILibraryIndexRepository indexes = mRepositories.GetLibraryIndexRepository(request.Profile);
        await mPageProcessor.PrepareSearchIndexesAsync(request.Profile,
                                                       request.LibraryId,
                                                       request.Version,
                                                       chunks,
                                                       shards,
                                                       indexes,
                                                       ct);
    }

    private bool IsCompatibleProcessingManifest(LibraryVersionRecord? prior) =>
        prior?.PublicationState == VersionPublicationState.Published
        && prior.EmbeddingProviderId.Equals(mPageProcessor.EmbeddingProviderId, StringComparison.Ordinal)
        && prior.EmbeddingModelName.Equals(mPageProcessor.EmbeddingModelName, StringComparison.Ordinal)
        && prior.EmbeddingDimensions == mPageProcessor.EmbeddingDimensions
        && string.Equals(prior.ClassifierBackend, mPageProcessor.ClassifierBackend, StringComparison.Ordinal)
        && string.Equals(prior.ClassifierModel, mPageProcessor.ClassifierModel, StringComparison.Ordinal);

    private static IReadOnlyList<DocChunk> OrderChunks(IEnumerable<DocChunk> chunks) =>
        chunks.OrderBy(chunk => chunk.PageUrl, StringComparer.Ordinal)
              .ThenBy(chunk => chunk.SectionPath, StringComparer.Ordinal)
              .ThenBy(chunk => chunk.Content, StringComparer.Ordinal)
              .ThenBy(chunk => chunk.Id, StringComparer.Ordinal)
              .ToList();

    private static bool SameEmbeddingInput(DocChunk current, DocChunk prior) =>
        current.Category == prior.Category
        && current.ParserVersion == prior.ParserVersion
        && current.PageTitle.Equals(prior.PageTitle, StringComparison.Ordinal)
        && current.Content.Equals(prior.Content, StringComparison.Ordinal)
        && string.Equals(current.SectionPath, prior.SectionPath, StringComparison.Ordinal);

    private static string? PublishedCurrentVersion(LibraryRecord? library)
    {
        string? result = library?.CurrentVersion;
        if (string.IsNullOrWhiteSpace(result))
            result = null;
        return result;
    }

    private static void EnsureComplete(DirectoryScanReport report)
    {
        DirectoryScanEntryResult? failure = report.Entries.FirstOrDefault(entry =>
                                                  entry.Status == DirectoryScanEntryStatus.Failed ||
                                                  IsUnsupportedSupportedFile(entry));
        if (report.Status == DirectoryScanStatus.Failed || failure != null)
        {
            string reasonCode = failure?.ReasonCode ?? report.ReasonCode;
            string detail = failure?.Detail ?? report.Detail;
            throw new DirectoryIngestionException(reasonCode, detail, failure?.RelativePath);
        }
    }

    private static bool IsUnsupportedSupportedFile(DirectoryScanEntryResult entry) =>
        entry.Kind == DirectoryScanEntryKind.File
        && entry.ReasonCode is DirectoryScanReasonCodes.FileTooLarge
            or DirectoryScanReasonCodes.FileEmpty;

    private static void ValidateRequest(DirectoryIngestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.LibraryId);
        ArgumentException.ThrowIfNullOrEmpty(request.Version);
        ArgumentException.ThrowIfNullOrEmpty(request.ScanRunId);
        ArgumentNullException.ThrowIfNull(request.Definition);
        if (!request.LibraryId.Equals(request.Definition.Id, StringComparison.Ordinal))
            throw new ArgumentException("The captured directory definition must match the requested library.",
                                        nameof(request));
        if (request.Definition.BindingStatus != DirectoryLibraryBindingStatus.Bound)
            throw new ArgumentException("The captured directory definition must be bound.", nameof(request));
    }

    private sealed record PipelineDocuments(IReadOnlyList<PageRecord> Pages,
                                            IReadOnlyList<DocChunk> Chunks,
                                            string? TaxonomyVersion);

}
