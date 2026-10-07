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
            string prompt = SubjectCatalogPrompt.Build(descriptor);
            IReadOnlyList<SubjectConcept> proposals =
                await SubjectResponseGenerator.GenerateValidatedAsync<SubjectCatalogResponse,
                    IReadOnlyList<SubjectConcept>>(mGenerator,
                                                   prompt,
                                                   response => ValidateProposals(response, descriptor),
                                                   ct);
            foreach(SubjectConcept proposal in proposals)
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

    private static IReadOnlyList<SubjectConcept> ValidateProposals(SubjectCatalogResponse response,
                                                                   SubjectDescriptor descriptor)
    {
        if (response.Concepts is not { Count: > 0 })
            throw new InvalidDataException("The subject catalog response did not contain any concepts.");
        if (response.Concepts.Count > SubjectClassificationLimits.MaxSecondarySubjects + 1)
            throw new InvalidDataException("The subject catalog response contains too many concepts for one document.");

        var result = response.Concepts.Select(proposal => ValidateProposal(proposal, descriptor)).ToList();
        return result;
    }

    private static SubjectConcept ValidateProposal(SubjectConceptResponse? proposal,
                                                     SubjectDescriptor descriptor)
    {
        if (proposal == null)
            throw new InvalidDataException("Subject concepts cannot be null.");

        string label = SubjectText.Bounded(proposal.Label,
                                           SubjectClassificationLimits.MaxHeadingCharacters);
        string description = SubjectText.Bounded(proposal.Description,
                                                 SubjectClassificationLimits.MaxSummaryCharacters);
        if (label.Length == 0 || description.Length == 0)
            throw new InvalidDataException("Every subject concept requires a label and description.");
        SubjectEvidence.Validate(proposal.Evidence, descriptor);

        var aliases = (proposal.Aliases ?? [])
                     .Select(alias => SubjectText.Bounded(alias,
                                                         SubjectClassificationLimits.MaxHeadingCharacters))
                     .Where(alias => alias.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(alias => alias, StringComparer.Ordinal)
                     .Take(SubjectClassificationLimits.MaxHeadingCount)
                     .ToList();
        return new SubjectConcept { Id = string.Empty, Label = label, Aliases = aliases, Description = description };
    }

    private void ReconcileProposal(List<SubjectConcept> concepts, SubjectConcept proposal)
    {
        IReadOnlyList<int> matches = FindSemanticMatches(concepts, proposal.Label);
        if (matches.Count > 1)
            throw new InvalidDataException("The subject classifier returned a concept that ambiguously matches multiple published concepts.");

        // Discovery can add a subject, but cannot rewrite an existing subject's meaning.
        // Model-supplied identifiers and aliases are never authority to replace a concept.
        if (matches.Count == 0)
            concepts.Add(proposal with { Id = mIdGenerator.CreateId() });
    }

    private static IReadOnlyList<int> FindSemanticMatches(IReadOnlyList<SubjectConcept> concepts,
                                                           string label)
    {
        var matches = new List<int>();
        for(var i = 0; i < concepts.Count; i++)
        {
            SubjectConcept concept = concepts[i];
            if (string.Equals(concept.Label, label, StringComparison.OrdinalIgnoreCase))
                matches.Add(i);
        }

        if (matches.Count == 0)
        {
            for(var i = 0; i < concepts.Count; i++)
            {
                if (concepts[i].Aliases.Contains(label, StringComparer.OrdinalIgnoreCase))
                    matches.Add(i);
            }
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
        public string? Label { get; init; }

        public IReadOnlyList<string>? Aliases { get; init; }

        public string? Description { get; init; }

        public IReadOnlyList<string>? Evidence { get; init; }
    }
}
