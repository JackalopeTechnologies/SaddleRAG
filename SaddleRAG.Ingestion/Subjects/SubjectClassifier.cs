// SubjectClassifier.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Classification;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Structured classifier for primary and secondary document subjects.</summary>
public sealed class SubjectClassifier : ISubjectClassifier
{
    public SubjectClassifier(IClassifierTextGenerator generator,
                             TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mGenerator = generator;
        mTimeProvider = timeProvider;
    }

    private readonly IClassifierTextGenerator mGenerator;
    private readonly TimeProvider mTimeProvider;

    public async Task<SubjectAssignmentRecord> ClassifyAsync(ISubjectAssignmentRepository repository,
                                                             SubjectDescriptor descriptor,
                                                             SubjectCatalogRecord catalog,
                                                             string version,
                                                             string scanRunId,
                                                             CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        if (catalog.Concepts.Count == 0)
            throw new ArgumentException("The subject catalog cannot be empty.", nameof(catalog));

        string prompt = SubjectClassificationPrompt.Build(descriptor, catalog);
        var knownIds = catalog.Concepts.Select(concept => concept.Id).ToHashSet(StringComparer.Ordinal);
        ValidatedClassification validated =
            await SubjectResponseGenerator.GenerateValidatedAsync<SubjectClassificationResponse,
                ValidatedClassification>(mGenerator,
                                         prompt,
                                         response => ValidateResponse(response, knownIds),
                                         ct);
        SubjectSelection primary = validated.Primary;
        List<SubjectSelection> secondary = validated.Secondary;

        var assignment = new SubjectAssignmentRecord
                             {
                                 Id = SubjectAssignmentRepository.MakeId(catalog.LibraryId,
                                                                         version,
                                                                         descriptor.DocumentRevisionId),
                                 LibraryId = catalog.LibraryId,
                                 Version = version,
                                 ScanRunId = scanRunId,
                                 DocumentId = descriptor.DocumentId,
                                 DocumentRevisionId = descriptor.DocumentRevisionId,
                                 TaxonomyVersion = catalog.TaxonomyVersion,
                                 Primary = primary,
                                 Secondary = secondary,
                                 NeedsReview = primary.Confidence <
                                               SubjectClassificationLimits.NeedsReviewConfidenceThreshold ||
                                               secondary.Any(selection => selection.Confidence <
                                                                          SubjectClassificationLimits.NeedsReviewConfidenceThreshold),
                                 Provenance = new SubjectClassifierProvenance
                                                  {
                                                      Backend = mGenerator.BackendName,
                                                      ModelId = mGenerator.ModelId,
                                                      PromptVersion = SubjectClassificationPrompt.PromptVersion,
                                                      GeneratedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime
                                                  }
                             };
        await repository.PersistAsync(assignment, ct);
        return assignment;
    }

    private static ValidatedClassification ValidateResponse(SubjectClassificationResponse response,
                                                            IReadOnlySet<string> knownIds)
    {
        if (response.Primary == null)
            throw new InvalidDataException("The subject classifier response requires one primary subject.");
        if (response.Secondary is { Count: > SubjectClassificationLimits.MaxSecondarySubjects })
            throw new InvalidDataException($"At most {SubjectClassificationLimits.MaxSecondarySubjects} secondary subjects are allowed.");

        SubjectSelection primary = ValidateSelection(response.Primary, knownIds);
        var secondary = (response.Secondary ?? []).Select(selection => ValidateSelection(selection, knownIds)).ToList();
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        if (!selectedIds.Add(primary.SubjectId) || secondary.Any(selection => !selectedIds.Add(selection.SubjectId)))
            throw new InvalidDataException("Primary and secondary subject ids must be unique.");

        var result = new ValidatedClassification(primary, secondary);
        return result;
    }

    private static SubjectSelection ValidateSelection(SubjectSelectionResponse? response,
                                                      IReadOnlySet<string> knownIds)
    {
        if (response == null)
            throw new InvalidDataException("Subject selections cannot be null.");
        if (string.IsNullOrWhiteSpace(response.SubjectId) || !knownIds.Contains(response.SubjectId))
            throw new InvalidDataException("The subject classifier selected an id outside the published catalog.");
        if (!float.IsFinite(response.Confidence) || response.Confidence is < 0f or > 1f)
            throw new InvalidDataException("Subject confidence must be between 0 and 1.");

        IReadOnlyList<string> rawEvidence = response.Evidence ?? [];
        if (rawEvidence.Count == 0)
            throw new InvalidDataException("Every selected subject requires supporting evidence.");
        if (rawEvidence.Count > SubjectClassificationLimits.MaxEvidenceCount)
            throw new InvalidDataException($"Subject evidence is limited to {SubjectClassificationLimits.MaxEvidenceCount} entries.");

        var evidence = new List<string>(rawEvidence.Count);
        var uniqueEvidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string item in rawEvidence)
        {
            string normalized = SubjectText.Normalize(item);
            if (normalized.Length == 0 || normalized.Length > SubjectClassificationLimits.MaxEvidenceCharacters)
                throw new InvalidDataException("Subject evidence is empty or exceeds the configured bound.");
            if (!uniqueEvidence.Add(normalized))
                throw new InvalidDataException("Subject evidence entries must be unique.");
            evidence.Add(normalized);
        }

        return new SubjectSelection
                   {
                       SubjectId = response.SubjectId,
                       Confidence = response.Confidence,
                       Evidence = evidence
                   };
    }

    private sealed record SubjectClassificationResponse
    {
        public SubjectSelectionResponse? Primary { get; init; }

        public IReadOnlyList<SubjectSelectionResponse?>? Secondary { get; init; }
    }

    private sealed record SubjectSelectionResponse
    {
        public string? SubjectId { get; init; }

        public float Confidence { get; init; }

        public IReadOnlyList<string>? Evidence { get; init; }
    }

    private sealed record ValidatedClassification(SubjectSelection Primary,
                                                  List<SubjectSelection> Secondary);
}
