// SubjectCatalogBuilder.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Ingestion.Classification;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Builds immutable library-scoped taxonomy revisions.</summary>
public sealed class SubjectCatalogBuilder
{
    public SubjectCatalogBuilder(IClassifierTextGenerator generator,
                                 ISubjectIdGenerator idGenerator,
                                 TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mGenerator = generator;
        mIdGenerator = idGenerator;
        mTimeProvider = timeProvider;
    }

    private readonly IClassifierTextGenerator mGenerator;
    private readonly ISubjectIdGenerator mIdGenerator;
    private readonly TimeProvider mTimeProvider;

    public async Task<SubjectCatalogRecord> ReconcileAsync(ISubjectCatalogRepository repository,
                                                           string libraryId,
                                                           string scanRunId,
                                                           IReadOnlyList<SubjectDescriptor> descriptors,
                                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        ArgumentNullException.ThrowIfNull(descriptors);
        if (descriptors.Count == 0)
            throw new ArgumentException("At least one subject descriptor is required.", nameof(descriptors));

        SubjectCatalogRecord? existing = await repository.GetLatestAsync(libraryId, ct);
        var concepts = existing?.Concepts.Select(CloneConcept).ToList() ?? [];

        foreach(SubjectDescriptor descriptor in descriptors.OrderBy(item => item.DocumentId,
                                                                     StringComparer.Ordinal))
        {
            string prompt = SubjectCatalogPrompt.Build(concepts, descriptor);
            SubjectCatalogResponse response =
                await SubjectResponseGenerator.GenerateAsync<SubjectCatalogResponse>(mGenerator,
                                                                                       prompt,
                                                                                       ct);
            if (response.Concepts is not { Count: > 0 })
                throw new InvalidDataException("The subject catalog response did not contain any concepts.");

            foreach(SubjectConceptResponse? proposal in response.Concepts)
                ReconcileProposal(concepts, proposal);
        }

        if (concepts.Count == 0)
            throw new InvalidDataException("Subject catalog reconciliation produced an empty catalog.");
        SubjectCatalogRecord result;
        if (existing != null && AreSemanticallyEqual(existing.Concepts, concepts))
            result = existing;
        else
        {
            int revision = (existing?.Revision ?? 0) + 1;
            string taxonomyVersion = $"taxonomy-{revision:D6}";
            var catalog = new SubjectCatalogRecord
                              {
                                  Id = SubjectCatalogRepository.MakeId(libraryId, taxonomyVersion),
                                  LibraryId = libraryId,
                                  Revision = revision,
                                  TaxonomyVersion = taxonomyVersion,
                                  ScanRunId = scanRunId,
                                  PublicationState = SubjectCatalogPublicationState.Candidate,
                                  PreviousTaxonomyVersion = existing?.TaxonomyVersion,
                                  Concepts = concepts.Select(CloneConcept).ToList(),
                                  Provenance = new SubjectClassifierProvenance
                                                   {
                                                       Backend = mGenerator.BackendName,
                                                       ModelId = mGenerator.ModelId,
                                                       PromptVersion = SubjectCatalogPrompt.PromptVersion,
                                                       GeneratedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime
                                                   },
                                  CreatedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime
                              };
            await repository.InsertRevisionAsync(catalog, ct);
            result = catalog;
        }

        return result;
    }

    private void ReconcileProposal(List<SubjectConcept> concepts, SubjectConceptResponse? proposal)
    {
        if (proposal == null)
            throw new InvalidDataException("Subject concepts cannot be null.");

        string label = SubjectText.Bounded(proposal.Label,
                                           SubjectClassificationLimits.MaxHeadingCharacters);
        string description = SubjectText.Bounded(proposal.Description,
                                                 SubjectClassificationLimits.MaxSummaryCharacters);
        if (label.Length == 0 || description.Length == 0)
            throw new InvalidDataException("Every subject concept requires a label and description.");

        var aliases = (proposal.Aliases ?? [])
                     .Select(alias => SubjectText.Bounded(alias,
                                                         SubjectClassificationLimits.MaxHeadingCharacters))
                     .Where(alias => alias.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(alias => alias, StringComparer.Ordinal)
                     .Take(SubjectClassificationLimits.MaxHeadingCount)
                     .ToList();
        IReadOnlyList<int> matches = FindSemanticMatches(concepts, label, aliases);
        if (matches.Count > 1)
            throw new InvalidDataException("The subject classifier returned a concept that ambiguously matches multiple published concepts.");

        int semanticIndex = matches.Count == 1 ? matches[0] : -1;
        string? proposedId = string.IsNullOrWhiteSpace(proposal.SubjectId)
                                 ? null
                                 : proposal.SubjectId.Trim();
        int idIndex = proposedId == null
                          ? -1
                          : concepts.FindIndex(concept => string.Equals(concept.Id,
                                                                         proposedId,
                                                                         StringComparison.Ordinal));
        int existingIndex;
        string id;
        if (semanticIndex >= 0)
        {
            SubjectConcept matched = concepts[semanticIndex];
            if (idIndex >= 0 && idIndex != semanticIndex)
            {
                throw new InvalidDataException(
                    "The subject classifier returned conflicting identity and semantic matches for a concept.");
            }

            existingIndex = semanticIndex;
            id = matched.Id;
        }
        else
        {
            if (proposedId != null)
            {
                if (idIndex < 0)
                    throw new InvalidDataException("The subject classifier returned an id outside the published catalog for a new concept.");

                existingIndex = idIndex;
                id = proposedId;
            }
            else
            {
                existingIndex = -1;
                id = mIdGenerator.CreateId();
            }
        }

        var reconciled = new SubjectConcept
                             {
                                 Id = id,
                                 Label = label,
                                 Aliases = aliases,
                                 Description = description
                             };
        if (existingIndex >= 0)
            concepts[existingIndex] = reconciled;
        else
            concepts.Add(reconciled);
    }

    private static IReadOnlyList<int> FindSemanticMatches(IReadOnlyList<SubjectConcept> concepts,
                                                           string label,
                                                           IReadOnlyList<string> aliases)
    {
        var proposedTerms = aliases.Append(label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = new List<int>();
        for(var i = 0; i < concepts.Count; i++)
        {
            SubjectConcept concept = concepts[i];
            if (concept.Aliases.Append(concept.Label).Any(proposedTerms.Contains))
                matches.Add(i);
        }

        return matches;
    }

    private static bool AreSemanticallyEqual(IReadOnlyList<SubjectConcept> first,
                                               IReadOnlyList<SubjectConcept> second)
    {
        bool result = first.Count == second.Count;
        if (result)
        {
            var secondById = second.ToDictionary(concept => concept.Id, StringComparer.Ordinal);
            foreach(SubjectConcept concept in first)
            {
                if (!secondById.TryGetValue(concept.Id, out SubjectConcept? candidate) ||
                    !string.Equals(concept.Label, candidate.Label, StringComparison.Ordinal) ||
                    !string.Equals(concept.Description, candidate.Description, StringComparison.Ordinal) ||
                    !CanonicalAliases(concept.Aliases).SequenceEqual(CanonicalAliases(candidate.Aliases),
                                                                      StringComparer.Ordinal))
                {
                    result = false;
                    break;
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> CanonicalAliases(IEnumerable<string> aliases) =>
        aliases.Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
               .ThenBy(alias => alias, StringComparer.Ordinal)
               .ToList();

    private static SubjectConcept CloneConcept(SubjectConcept concept) =>
        concept with { Aliases = CanonicalAliases(concept.Aliases) };

    private sealed record SubjectCatalogResponse
    {
        public IReadOnlyList<SubjectConceptResponse?>? Concepts { get; init; }
    }

    private sealed record SubjectConceptResponse
    {
        public string? SubjectId { get; init; }

        public string? Label { get; init; }

        public IReadOnlyList<string>? Aliases { get; init; }

        public string? Description { get; init; }
    }
}
