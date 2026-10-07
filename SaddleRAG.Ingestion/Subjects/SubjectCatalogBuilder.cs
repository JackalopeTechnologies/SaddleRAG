// SubjectCatalogBuilder.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
                                 TimeProvider timeProvider,
                                 ILogger<SubjectCatalogBuilder>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mGenerator = generator;
        mIdGenerator = idGenerator;
        mTimeProvider = timeProvider;
        mLogger = logger ?? NullLogger<SubjectCatalogBuilder>.Instance;
    }

    private readonly IClassifierTextGenerator mGenerator;
    private readonly ISubjectIdGenerator mIdGenerator;
    private readonly TimeProvider mTimeProvider;
    private readonly ILogger<SubjectCatalogBuilder> mLogger;

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
        SubjectCatalogReconciliation reconciliation = await ReconcileDocumentsAsync(repository,
            libraryId, scanRunId, descriptors, ct);
        return reconciliation.Catalog;
    }

    /// <summary>Discovers subjects once and retains each document's selections for publication.</summary>
    public async Task<SubjectCatalogReconciliation> ReconcileDocumentsAsync(
        ISubjectCatalogRepository repository,
        string libraryId,
        string scanRunId,
        IReadOnlyList<SubjectDescriptor> descriptors,
        CancellationToken ct = default,
        Action<string, int>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(scanRunId);
        ArgumentNullException.ThrowIfNull(descriptors);
        if (descriptors.Count == 0)
            throw new ArgumentException("At least one subject descriptor is required.", nameof(descriptors));
        if (descriptors.Select(descriptor => descriptor.DocumentRevisionId).Distinct(StringComparer.Ordinal).Count()
            != descriptors.Count)
            throw new ArgumentException("Every document revision must be unique.", nameof(descriptors));

        SubjectCatalogRecord? existing = await repository.GetLatestAsync(libraryId, ct);
        var concepts = existing?.Concepts.Select(CloneConcept).ToList() ?? [];
        var documents = new Dictionary<string, (string DocumentId, IReadOnlyList<SubjectSelection> Selections)>(StringComparer.Ordinal);

        foreach(SubjectDescriptor descriptor in descriptors.OrderBy(item => item.DocumentId,
                                                                     StringComparer.Ordinal))
        {
            var elapsed = Stopwatch.StartNew();
            onProgress?.Invoke(descriptor.RelativePath, documents.Count);
            mLogger.LogInformation("Subject labeling started for {DocumentPath} using {Backend}/{Model}",
                descriptor.RelativePath, mGenerator.BackendName, mGenerator.ModelId);
            string prompt = SubjectCatalogPrompt.Build(descriptor);
            IReadOnlyList<ValidatedProposal> proposals =
                await SubjectResponseGenerator.GenerateValidatedAsync<SubjectCatalogResponse,
                    IReadOnlyList<ValidatedProposal>>(mGenerator,
                                                   prompt,
                                                   response => ValidateProposals(response, descriptor),
                                                   ct,
                                                   SubjectResponseSchema.Catalog(descriptor));
            var selections = new List<SubjectSelection>();
            foreach(ValidatedProposal proposal in proposals)
            {
                string subjectId = ReconcileProposal(concepts, proposal.Concept);
                if (selections.Any(selection => selection.SubjectId.Equals(subjectId, StringComparison.Ordinal)))
                    throw new InvalidDataException("A document cannot select the same subject more than once.");
                selections.Add(new SubjectSelection
                                   {
                                       SubjectId = subjectId,
                                       Confidence = proposal.Confidence,
                                       Evidence = proposal.Evidence
                                   });
            }
            documents.Add(descriptor.DocumentRevisionId, (descriptor.DocumentId, selections));
            onProgress?.Invoke(descriptor.RelativePath, documents.Count);
            mLogger.LogInformation("Subject labeling completed for {DocumentPath} in {ElapsedSeconds:F1}s; {Completed}/{Total} documents",
                descriptor.RelativePath, elapsed.Elapsed.TotalSeconds, documents.Count, descriptors.Count);
        }

        if (concepts.Count == 0)
            throw new InvalidDataException("Subject catalog reconciliation produced an empty catalog.");
        var provenance = new SubjectClassifierProvenance
                             {
                                 Backend = mGenerator.BackendName,
                                 ModelId = mGenerator.ModelId,
                                 PromptVersion = SubjectCatalogPrompt.PromptVersion,
                                 GeneratedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime
                             };
        SubjectCatalogRecord catalog;
        if (existing != null && AreSemanticallyEqual(existing.Concepts, concepts))
            catalog = existing;
        else
        {
            int revision = (existing?.Revision ?? 0) + 1;
            string taxonomyVersion = $"taxonomy-{revision:D6}";
            catalog = new SubjectCatalogRecord
                              {
                                  Id = SubjectCatalogRepository.MakeId(libraryId, taxonomyVersion),
                                  LibraryId = libraryId,
                                  Revision = revision,
                                  TaxonomyVersion = taxonomyVersion,
                                  ScanRunId = scanRunId,
                                  PublicationState = SubjectCatalogPublicationState.Candidate,
                                  PreviousTaxonomyVersion = existing?.TaxonomyVersion,
                                  Concepts = concepts.Select(CloneConcept).ToList(),
                                  Provenance = provenance,
                                  CreatedAtUtc = mTimeProvider.GetUtcNow().UtcDateTime
                              };
            await repository.InsertRevisionAsync(catalog, ct);
        }

        var result = new SubjectCatalogReconciliation(catalog, documents, provenance);
        return result;
    }

    private static IReadOnlyList<ValidatedProposal> ValidateProposals(SubjectCatalogResponse response,
                                                                   SubjectDescriptor descriptor)
    {
        if (response.Concepts is not { Count: > 0 })
            throw new InvalidDataException("The subject catalog response did not contain any concepts.");
        if (response.Concepts.Count > SubjectClassificationLimits.MaxSecondarySubjects + 1)
            throw new InvalidDataException("The subject catalog response contains too many concepts for one document.");

        var result = response.Concepts.Select(proposal => ValidateProposal(proposal, descriptor)).ToList();
        return result;
    }

    private static ValidatedProposal ValidateProposal(SubjectConceptResponse? proposal,
                                                     SubjectDescriptor descriptor)
    {
        if (proposal == null)
            throw new InvalidDataException("Subject concepts cannot be null.");

        string label = SubjectText.Bounded(proposal.Label,
                                           SubjectClassificationLimits.MaxHeadingCharacters);
        if (label.Length == 0)
            throw new InvalidDataException("Every subject concept requires a label.");
        if (proposal.Confidence is not { } confidence || !float.IsFinite(confidence) || confidence is < 0f or > 1f)
            throw new InvalidDataException("Every subject requires confidence between 0 and 1.");
        IReadOnlyList<string> evidence = SubjectEvidence.Validate(proposal.Evidence, descriptor);

        var aliases = (proposal.Aliases ?? [])
                     .Select(alias => SubjectText.Bounded(alias,
                                                         SubjectClassificationLimits.MaxHeadingCharacters))
                     .Where(alias => alias.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(alias => alias, StringComparer.Ordinal)
                     .Take(SubjectClassificationLimits.MaxHeadingCount)
                     .ToList();
        var concept = new SubjectConcept { Id = string.Empty, Label = label, Aliases = aliases, Description = evidence[0] };
        return new ValidatedProposal(concept, confidence, evidence);
    }

    private string ReconcileProposal(List<SubjectConcept> concepts, SubjectConcept proposal)
    {
        IReadOnlyList<int> matches = FindSemanticMatches(concepts, proposal.Label);
        if (matches.Count > 1)
            throw new InvalidDataException("The subject classifier returned a concept that ambiguously matches multiple published concepts.");

        // Discovery can add a subject, but cannot rewrite an existing subject's meaning.
        // Model-supplied identifiers and aliases are never authority to replace a concept.
        if (matches.Count == 0)
            concepts.Add(proposal with { Id = mIdGenerator.CreateId() });
        string result = matches.Count == 0 ? concepts[^1].Id : concepts[matches[0]].Id;
        return result;
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

        public float? Confidence { get; init; }

        public IReadOnlyList<string>? Evidence { get; init; }
    }

    private sealed record ValidatedProposal(SubjectConcept Concept,
                                            float Confidence,
                                            IReadOnlyList<string> Evidence);
}
