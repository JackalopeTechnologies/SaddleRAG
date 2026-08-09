// DirectoryPackagingFixtures.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.Cryptography;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Packaging.Fixtures;

internal static class DirectoryPackagingFixtures
{
    internal static LibraryRecord Library(string libraryId = LibraryId, string version = Version) => new()
        {
            Id = libraryId,
            Name = "Owned service manuals",
            Hint = "Stage 7 package fixture",
            CurrentVersion = version,
            AllVersions = [version]
        };

    internal static LibraryVersionRecord LibraryVersion(string libraryId = LibraryId,
                                                        string version = Version) => new()
        {
            Id = $"{libraryId}/{version}",
            LibraryId = libraryId,
            Version = version,
            ScrapedAt = RecordedAt,
            PageCount = 1,
            ChunkCount = 1,
            EmbeddingProviderId = EmbeddingProviderId,
            EmbeddingModelName = EmbeddingModelName,
            EmbeddingDimensions = EmbeddingDimensions,
            SubjectTaxonomyVersion = TaxonomyVersion,
            PublicationState = VersionPublicationState.Published
        };

    internal static DirectoryLibraryDefinition DirectoryDefinition(string libraryId = LibraryId,
                                                                    string version = Version) => new()
        {
            Id = libraryId,
            RootPath = RootPath,
            Recursive = true,
            AllowedExtensions = [".docx", ".pdf", ".txt"],
            ExclusionPatterns = ["**/.git/**", "**/bin/**"],
            BindingStatus = DirectoryLibraryBindingStatus.Bound,
            RegisteredAtUtc = RecordedAt,
            LastPublishedAtUtc = RecordedAt,
            LastPublishedVersion = version
        };

    internal static SourceDocumentRecord Source(string libraryId = LibraryId,
                                                string version = Version,
                                                string documentId = DocumentId) => new()
        {
            Id = documentId,
            LibraryId = libraryId,
            NormalizedRelativePath = RelativePath,
            DisplayRelativePath = RelativePath,
            DisplayName = "Pump Manual.pdf",
            SourceUri = SourceUri(libraryId, documentId),
            MediaType = "application/pdf",
            FirstSeenVersion = version,
            LastSeenVersion = version,
            CreatedAtUtc = RecordedAt,
            UpdatedAtUtc = RecordedAt
        };

    internal static DocumentRevisionRecord Revision(string libraryId = LibraryId,
                                                    string version = Version,
                                                    string documentId = DocumentId,
                                                    byte[]? original = null,
                                                    byte[]? extraction = null) => new()
        {
            Id = RevisionId(libraryId, version, documentId),
            DocumentId = documentId,
            LibraryId = libraryId,
            Version = version,
            ScanRunId = $"scan-{libraryId}-{version}",
            State = DocumentRevisionState.Published,
            SourceModifiedAtUtc = RecordedAt,
            AcquiredAtUtc = RecordedAt,
            OriginalArtifactHash = Hash(original ?? OriginalBytes),
            OriginalByteLength = (original ?? OriginalBytes).LongLength,
            OriginalMediaType = "application/pdf",
            ExtractionArtifactHash = Hash(extraction ?? ExtractionBytes),
            ExtractionByteLength = (extraction ?? ExtractionBytes).LongLength,
            ExtractionMediaType = "application/json",
            ExtractionProvenance = ExtractionProvenance(),
            PublishedAtUtc = RecordedAt
        };

    internal static SubjectCatalogRecord Catalog(string libraryId = LibraryId) => new()
        {
            Id = SubjectCatalogRepository.MakeId(libraryId, TaxonomyVersion),
            LibraryId = libraryId,
            Revision = 1,
            TaxonomyVersion = TaxonomyVersion,
            ScanRunId = $"scan-{libraryId}-{Version}",
            Concepts = Concepts,
            Provenance = ClassifierProvenance(),
            CreatedAtUtc = RecordedAt
        };

    internal static SubjectAssignmentRecord Assignment(string libraryId = LibraryId,
                                                       string version = Version,
                                                       string documentId = DocumentId) => new()
        {
            Id = SubjectAssignmentRepository.MakeId(libraryId,
                                                    version,
                                                    RevisionId(libraryId, version, documentId)),
            LibraryId = libraryId,
            Version = version,
            ScanRunId = $"scan-{libraryId}-{version}",
            DocumentId = documentId,
            DocumentRevisionId = RevisionId(libraryId, version, documentId),
            TaxonomyVersion = TaxonomyVersion,
            Primary = new SubjectSelection
                          {
                              SubjectId = SubjectId,
                              Confidence = 0.96f,
                              Evidence = ["hydraulic pump calibration"]
                          },
            Secondary =
            [
                new SubjectSelection
                    {
                        SubjectId = SecondarySubjectId,
                        Confidence = 0.74f,
                        Evidence = ["safety pressure limits"]
                    }
            ],
            NeedsReview = false,
            Provenance = ClassifierProvenance()
        };

    internal static PageRecord Page(string libraryId = LibraryId,
                                    string version = Version,
                                    string documentId = DocumentId) => new()
        {
            Id = PageId(libraryId, version, documentId, sectionOrder: 1),
            LibraryId = libraryId,
            Version = version,
            Url = $"{SourceUri(libraryId, documentId)}#section-0001",
            Title = "Pump Manual",
            Category = DocCategory.HowTo,
            RawContent = SearchMarker,
            FetchedAt = RecordedAt,
            ContentHash = Hash(System.Text.Encoding.UTF8.GetBytes(SearchMarker)),
            DocumentSource = Provenance(libraryId, version, documentId),
            SubjectIds = [SubjectId, SecondarySubjectId],
            SubjectTaxonomyVersion = TaxonomyVersion
        };

    internal static DocChunk Chunk(string libraryId = LibraryId,
                                   string version = Version,
                                   string documentId = DocumentId) => new()
        {
            Id = $"{libraryId}/{version}/stage7-chunk",
            LibraryId = libraryId,
            Version = version,
            PageUrl = $"{SourceUri(libraryId, documentId)}#section-0001",
            PageTitle = "Pump Manual",
            Category = DocCategory.HowTo,
            Content = SearchMarker,
            TokenCount = 7,
            SectionPath = "Hydraulics > Pump calibration",
            Embedding = [1.0f, 0.0f],
            DocumentSource = Provenance(libraryId, version, documentId),
            SubjectIds = [SubjectId, SecondarySubjectId],
            SubjectTaxonomyVersion = TaxonomyVersion
        };

    internal static DocumentProvenance Provenance(string libraryId = LibraryId,
                                                  string version = Version,
                                                  string documentId = DocumentId) => new()
        {
            DocumentId = documentId,
            RevisionId = RevisionId(libraryId, version, documentId),
            SourceUri = SourceUri(libraryId, documentId),
            RelativePath = RelativePath,
            PageStart = 12,
            PageEnd = 13,
            Heading = "Hydraulic pump calibration"
        };

    internal static DocumentExtractionProvenance ExtractionProvenance() => new()
        {
            ExtractorName = "docling",
            ExtractorVersion = "2.50.0",
            ConfigurationHash = "stage7-docling-config",
            UsedOcr = true,
            QualityScore = 0.985,
            Warnings = ["fixture warning retained exactly"]
        };

    internal static SubjectClassifierProvenance ClassifierProvenance() => new()
        {
            Backend = "onnx",
            ModelId = "stage7-subject-model",
            PromptVersion = "subject-v1",
            GeneratedAtUtc = RecordedAt
        };

    internal static string RevisionId(string libraryId = LibraryId,
                                      string version = Version,
                                      string documentId = DocumentId) =>
        SourceDocumentRepository.MakeRevisionId(libraryId, version, documentId);

    internal static string PageId(string libraryId,
                                  string version,
                                  string documentId,
                                  int sectionOrder)
    {
        string identity = string.Join('\u001f',
                                      libraryId,
                                      version,
                                      documentId,
                                      sectionOrder.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return $"document-page-{Hash(System.Text.Encoding.UTF8.GetBytes(identity))}";
    }

    internal static string SourceUri(string libraryId = LibraryId, string documentId = DocumentId) =>
        $"saddlerag://library/{libraryId}/documents/{documentId}";

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static readonly IReadOnlyList<SubjectConcept> Concepts =
    [
        new SubjectConcept
            {
                Id = SubjectId,
                Label = "Hydraulics",
                Aliases = ["fluid power"],
                Description = "Hydraulic calibration and service procedures"
            },
        new SubjectConcept
            {
                Id = SecondarySubjectId,
                Label = "Safety",
                Aliases = ["safe operation"],
                Description = "Safety limits and protective procedures"
            }
    ];
    internal static readonly byte[] OriginalBytes =
        [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x00, 0x80, 0xFF, 0x0A];
    internal static readonly byte[] ExtractionBytes =
        "{\"title\":\"Pump Manual\",\"markdown\":\"exact extracted text\"}"u8.ToArray();
    internal static readonly DateTime RecordedAt = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
    internal const string LibraryId = "stage7-package-library";
    internal const string Version = "2026-08-04";
    internal const string DocumentId =
        "source-document-42d1e592af3774dd59b75106705a9cf585c4a593623c095a064d712d625535bd";
    internal const string RelativePath = "manuals/Pump Manual.pdf";
    internal const string RootPath = "C:\\Users\\Doug\\Private Manuals";
    internal const string TaxonomyVersion = "taxonomy-stage7";
    internal const string SubjectId = "hydraulics";
    internal const string SecondarySubjectId = "safety";
    internal const string SearchMarker = "Stage 7 package hydraulic calibration marker";
    internal const string EmbeddingProviderId = "stage7-embedding";
    internal const string EmbeddingModelName = "stage7-embedding-model";
    internal const int EmbeddingDimensions = 2;
}
