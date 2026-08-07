// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Documents.Intake;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

internal static class SubjectTestData
{
    public static readonly DateTime GeneratedAtUtc = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    public static SubjectDescriptor Descriptor(string documentId = "document-hydraulics",
                                               string revisionId = "revision-hydraulics") =>
        new()
            {
                DocumentId = documentId,
                DocumentRevisionId = revisionId,
                Title = "Hydraulic pump safety",
                RelativePath = "maintenance/hydraulics-safety.pdf",
                Headings = ["Overview", "Lockout", "Pump service"],
                TableOfContents = "Overview\nLockout\nPump service",
                Summary = "Safe service of hydraulic pumps and pressure circuits.",
                StratifiedSections =
                [
                    "Overview: hydraulic pressure safety.",
                    "Lockout: isolate stored energy.",
                    "Pump service: inspect the pump."
                ]
            };

    public static SubjectCatalogRecord Catalog(string taxonomyVersion = "taxonomy-000001") =>
        new()
            {
                Id = $"manual-library/{taxonomyVersion}",
                LibraryId = "manual-library",
                Revision = 1,
                TaxonomyVersion = taxonomyVersion,
                Concepts =
                [
                    new SubjectConcept
                        {
                            Id = "subject-hydraulics",
                            Label = "Hydraulics",
                            Aliases = ["fluid power", "hydraulic"],
                            Description = "Hydraulic power, pumps, and circuits."
                        },
                    new SubjectConcept
                        {
                            Id = "subject-safety",
                            Label = "Safety",
                            Aliases = ["lockout", "LOTO"],
                            Description = "Safe work and energy isolation."
                        },
                    new SubjectConcept
                        {
                            Id = "subject-electrical",
                            Label = "Electrical controls",
                            Aliases = ["controls"],
                            Description = "Electrical control systems."
                        }
                ],
                Provenance = Provenance("subject-catalog-v1"),
                CreatedAtUtc = GeneratedAtUtc
            };

    public static SubjectClassifierProvenance Provenance(string promptVersion) =>
        new()
            {
                Backend = "scripted",
                ModelId = "scripted-subject-model",
                PromptVersion = promptVersion,
                GeneratedAtUtc = GeneratedAtUtc
            };

    public static SourceDocumentRecord SourceDocument() =>
        new()
            {
                Id = "document-hydraulics",
                LibraryId = "manual-library",
                NormalizedRelativePath = "maintenance/hydraulics-safety.pdf",
                DisplayRelativePath = "maintenance/hydraulics-safety.pdf",
                DisplayName = "hydraulics-safety.pdf",
                SourceUri = "saddlerag://manual-library/documents/document-hydraulics",
                MediaType = "application/pdf",
                FirstSeenVersion = "2026-08-04",
                CreatedAtUtc = GeneratedAtUtc
            };

    public static DocumentRevisionRecord Revision() =>
        new()
            {
                Id = "revision-hydraulics",
                DocumentId = "document-hydraulics",
                LibraryId = "manual-library",
                Version = "2026-08-04",
                ScanRunId = "scan-1",
                State = DocumentRevisionState.Candidate,
                AcquiredAtUtc = GeneratedAtUtc,
                OriginalArtifactHash = "hash",
                OriginalByteLength = 100,
                OriginalMediaType = "application/pdf"
            };

    public static DocumentIntakeResult Intake(IReadOnlyList<DocumentIntakeSection> sections,
                                              string title = "Hydraulic pump safety") =>
        new(true,
            "ok",
            string.Empty,
            title,
            sections,
            ReadOnlyMemory<byte>.Empty,
            "text/markdown",
            new DocumentExtractionProvenance
                {
                    ExtractorName = "scripted",
                    ExtractorVersion = "1.0",
                    UsedOcr = false
                });

    public static DocChunk Chunk(string id,
                                 string revisionId,
                                 IReadOnlyList<string>? subjectIds = null,
                                 string? taxonomyVersion = "taxonomy-000001") =>
        new()
            {
                Id = id,
                LibraryId = "manual-library",
                Version = "2026-08-04",
                PageUrl = $"saddlerag://manual-library/chunks/{id}",
                PageTitle = "Manual",
                Category = DocCategory.HowTo,
                Content = "Service instructions.",
                Embedding = [1.0f, 0.0f],
                TokenCount = 3,
                SubjectIds = subjectIds ?? ["subject-hydraulics"],
                SubjectTaxonomyVersion = taxonomyVersion,
                DocumentSource = new DocumentProvenance
                    {
                        DocumentId = $"document-{id}",
                        RevisionId = revisionId,
                        SourceUri = $"saddlerag://manual-library/documents/document-{id}",
                        RelativePath = $"manuals/{id}.pdf"
                    }
            };
}

internal sealed class ScriptedSubjectGenerator : IClassifierTextGenerator
{
    public ScriptedSubjectGenerator(params string[] responses)
    {
        mResponses = new Queue<string>(responses);
    }

    private readonly Queue<string> mResponses;

    public List<string> Prompts { get; } = [];

    public string BackendName => "scripted";

    public string ModelId => "scripted-subject-model";

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Prompts.Add(prompt);
        if (mResponses.Count == 0)
            throw new InvalidOperationException("No scripted classifier response remains.");

        return Task.FromResult(mResponses.Dequeue());
    }
}

internal sealed class SequenceSubjectIdGenerator : ISubjectIdGenerator
{
    public SequenceSubjectIdGenerator(params string[] ids)
    {
        mIds = new Queue<string>(ids);
    }

    private readonly Queue<string> mIds;

    public string CreateId() => mIds.Dequeue();
}

internal sealed class FixedSubjectTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(SubjectTestData.GeneratedAtUtc);
}

internal sealed class InMemorySubjectCatalogRepository : ISubjectCatalogRepository
{
    private readonly List<SubjectCatalogRecord> mCatalogs = [];

    public int GetManyCallCount { get; private set; }

    public IReadOnlyList<SubjectCatalogRecord> Inserted => mCatalogs;

    public Task<SubjectCatalogRecord?> GetLatestAsync(string libraryId, CancellationToken ct = default)
    {
        SubjectCatalogRecord? result = mCatalogs
                                      .Where(c => c.LibraryId == libraryId)
                                      .OrderByDescending(c => c.Revision)
                                      .FirstOrDefault();
        return Task.FromResult(result);
    }

    public Task<SubjectCatalogRecord?> GetAsync(string libraryId,
                                                string taxonomyVersion,
                                                CancellationToken ct = default)
    {
        SubjectCatalogRecord? result = mCatalogs.FirstOrDefault(c => c.LibraryId == libraryId &&
                                                                     c.TaxonomyVersion == taxonomyVersion);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SubjectCatalogRecord>> GetManyAsync(IReadOnlyCollection<SubjectCatalogKey> keys,
                                                                   CancellationToken ct = default)
    {
        GetManyCallCount++;
        var requested = keys.ToHashSet();
        IReadOnlyList<SubjectCatalogRecord> result = mCatalogs
                                                     .Where(c => requested.Contains(new SubjectCatalogKey(c.LibraryId,
                                                                                                           c.TaxonomyVersion)))
                                                     .ToList();
        return Task.FromResult(result);
    }

    public Task InsertRevisionAsync(SubjectCatalogRecord catalog, CancellationToken ct = default)
    {
        mCatalogs.Add(catalog);
        return Task.CompletedTask;
    }

    public Task<long> DeleteCandidateScanRunAsync(string libraryId,
                                                  string scanRunId,
                                                  string? deletingVersion,
                                                  CancellationToken ct = default)
    {
        int removed = mCatalogs.RemoveAll(catalog => catalog.LibraryId == libraryId &&
                                                       catalog.ScanRunId == scanRunId);
        return Task.FromResult((long)removed);
    }

    public Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        int removed = mCatalogs.RemoveAll(catalog => catalog.LibraryId == libraryId);
        return Task.FromResult((long)removed);
    }

    public void Seed(SubjectCatalogRecord catalog) => mCatalogs.Add(catalog);
}

internal sealed class InMemorySubjectAssignmentRepository : ISubjectAssignmentRepository
{
    private readonly List<SubjectAssignmentRecord> mAssignments = [];

    public int BulkReadCallCount { get; private set; }

    public IReadOnlyList<SubjectAssignmentRecord> Persisted => mAssignments;

    public Task PersistAsync(SubjectAssignmentRecord assignment, CancellationToken ct = default)
    {
        int index = mAssignments.FindIndex(a => a.Id == assignment.Id);
        if (index >= 0)
            mAssignments[index] = assignment;
        else
            mAssignments.Add(assignment);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SubjectAssignmentRecord>> GetByDocumentRevisionIdsAsync(
        IReadOnlyCollection<string> documentRevisionIds,
        CancellationToken ct = default)
    {
        BulkReadCallCount++;
        var requested = documentRevisionIds.ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<SubjectAssignmentRecord> result = mAssignments
                                                        .Where(a => requested.Contains(a.DocumentRevisionId))
                                                        .ToList();
        return Task.FromResult(result);
    }

    public Task<long> DeleteScanRunAsync(string libraryId, string scanRunId, CancellationToken ct = default)
    {
        int removed = mAssignments.RemoveAll(a => a.LibraryId == libraryId && a.ScanRunId == scanRunId);
        return Task.FromResult((long)removed);
    }

    public Task<long> DeleteLibraryAsync(string libraryId, CancellationToken ct = default)
    {
        int removed = mAssignments.RemoveAll(assignment => assignment.LibraryId == libraryId);
        return Task.FromResult((long)removed);
    }

    public void Seed(SubjectAssignmentRecord assignment) => mAssignments.Add(assignment);
}
