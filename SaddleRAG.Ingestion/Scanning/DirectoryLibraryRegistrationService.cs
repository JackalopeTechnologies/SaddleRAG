// DirectoryLibraryRegistrationService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Validates and records a user-selected directory without scanning it.</summary>
public sealed class DirectoryLibraryRegistrationService : IDirectoryLibraryRegistrationService
{
    public DirectoryLibraryRegistrationService(RepositoryFactory repositoryFactory,
                                               DirectoryRootValidator rootValidator,
                                               TimeProvider timeProvider,
                                               ILibraryIngestionModeLeaseManager modeLeaseManager)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(rootValidator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(modeLeaseManager);
        mRepositoryFactory = repositoryFactory;
        mRootValidator = rootValidator;
        mTimeProvider = timeProvider;
        mModeLeaseManager = modeLeaseManager;
    }

    private readonly RepositoryFactory mRepositoryFactory;
    private readonly DirectoryRootValidator mRootValidator;
    private readonly TimeProvider mTimeProvider;
    private readonly ILibraryIngestionModeLeaseManager mModeLeaseManager;

    public async Task<DirectoryRegistrationResult> RegisterAsync(DirectoryRegistrationRequest request,
                                                                 string? profile,
                                                                 CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.LibraryId);
        ArgumentNullException.ThrowIfNull(request.RootPath);

        var validation = mRootValidator.Validate(request.RootPath);
        DirectoryRegistrationResult result;
        if (!validation.Succeeded)
        {
            result = new DirectoryRegistrationResult(DirectoryRegistrationStatuses.Failed,
                                                     request.LibraryId,
                                                     validation.ReasonCode,
                                                     validation.Detail);
        }
        else
        {
            result = await RegisterValidatedAsync(request, profile, validation.CanonicalRoot, ct);
        }

        return result;
    }

    private async Task<DirectoryRegistrationResult> RegisterValidatedAsync(DirectoryRegistrationRequest request,
                                                                            string? profile,
                                                                            string canonicalRoot,
                                                                            CancellationToken ct)
    {
        ILibraryIngestionModeLease? modeLease = await mModeLeaseManager.TryAcquireAsync(
                                                    profile,
                                                    request.LibraryId,
                                                    LibraryIngestionMode.Directory,
                                                    ct);
        DirectoryRegistrationResult result;
        if (modeLease == null)
        {
            result = FailedModeConflict(request.LibraryId);
        }
        else
        {
            await using(modeLease)
            {
                using CancellationTokenSource operation =
                    CancellationTokenSource.CreateLinkedTokenSource(ct, modeLease.OwnershipLostToken);
                result = await RegisterUnderModeLeaseAsync(request,
                                                           profile,
                                                           canonicalRoot,
                                                           modeLease,
                                                           operation.Token);
            }
        }

        return result;
    }

    private async Task<DirectoryRegistrationResult> RegisterUnderModeLeaseAsync(
        DirectoryRegistrationRequest request,
        string? profile,
        string canonicalRoot,
        ILibraryIngestionModeLease modeLease,
        CancellationToken ct)
    {
        var sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
        var modes = mRepositoryFactory.GetLibraryIngestionModeRepository(profile);
        DirectoryLibraryDefinition? existingDefinition =
            await sources.GetDirectoryDefinitionAsync(request.LibraryId, ct);
        LibraryIngestionDataEvidence? evidence =
            modeLease.OwnershipStateAtAcquisition == LibraryIngestionOwnershipState.Reserved &&
            existingDefinition == null
                ? await modes.GetLibraryDataEvidenceAsync(request.LibraryId, ct)
                : null;
        DirectoryRegistrationResult result;
        if (evidence is { HasOwnedContent: true } ownedEvidence)
        {
            result = await ReconcileOwnedContentAsync(request.LibraryId,
                                                      ownedEvidence,
                                                      modeLease,
                                                      ct);
        }
        else
        {
            bool renewed = await modeLease.TryRenewAsync(ct);
            if (!renewed)
                throw new InvalidOperationException(ModeLeaseLostDetail);

            var definition = CreateDefinition(request, canonicalRoot);
            await sources.RegisterDirectoryDefinitionAsync(definition, ct);
            bool committed = await modeLease.TryCommitAsync(ct);
            if (!committed)
                throw new InvalidOperationException(ModeLeaseLostDetail);
            result = new DirectoryRegistrationResult(DirectoryRegistrationStatuses.Registered,
                                                     request.LibraryId);
        }

        return result;
    }

    private static async Task<DirectoryRegistrationResult> ReconcileOwnedContentAsync(
        string libraryId,
        LibraryIngestionDataEvidence evidence,
        ILibraryIngestionModeLease modeLease,
        CancellationToken ct)
    {
        if (evidence.HasDirectoryDefinition)
        {
            bool committed = await modeLease.TryCommitAsync(ct);
            if (!committed)
                throw new InvalidOperationException(ModeLeaseLostDetail);
        }
        else
        {
            if (evidence.HasLibraryRecord)
            {
                bool reconciled = await modeLease.TryReconcileReservedModeAsync(LibraryIngestionMode.Web, ct);
                if (!reconciled)
                    throw new InvalidOperationException(ModeLeaseLostDetail);
            }
        }

        DirectoryRegistrationResult result = FailedModeConflict(libraryId);
        return result;
    }

    private static DirectoryRegistrationResult FailedModeConflict(string libraryId) =>
        new(DirectoryRegistrationStatuses.Failed,
            libraryId,
            ModeConflictReasonCode,
            ModeConflictDetail);

    private DirectoryLibraryDefinition CreateDefinition(DirectoryRegistrationRequest request,
                                                         string canonicalRoot) =>
        new()
            {
                Id = request.LibraryId,
                RootPath = canonicalRoot,
                Name = NormalizeOptional(request.Name),
                Hint = NormalizeOptional(request.Hint),
                Recursive = request.Recursive,
                AllowedExtensions = NormalizeExtensions(request.AllowedExtensions),
                ExclusionPatterns = NormalizeExclusions(request.ExclusionPatterns),
                BindingStatus = DirectoryLibraryBindingStatus.Bound,
                RegisteredAtUtc = mTimeProvider.GetUtcNow().UtcDateTime
            };

    private static string? NormalizeOptional(string? value)
    {
        string? result = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return result;
    }

    private static IReadOnlyList<string> NormalizeExtensions(IReadOnlyList<string>? extensions)
    {
        IReadOnlyList<string> source = extensions is { Count: > 0 }
                                           ? extensions
                                           : DirectoryScanLimits.SupportedExtensions;
        var result = source.Where(extension => !string.IsNullOrWhiteSpace(extension))
                           .Select(NormalizeExtension)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .Order(StringComparer.Ordinal)
                           .ToList();
        return result;
    }

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        var result = (trimmed.StartsWith('.') ? trimmed : $".{trimmed}").ToLowerInvariant();
        return result;
    }

    private static IReadOnlyList<string> NormalizeExclusions(IReadOnlyList<string>? exclusions)
    {
        var result = (exclusions ?? [])
                     .Where(exclusion => !string.IsNullOrWhiteSpace(exclusion))
                     .Select(exclusion => exclusion.Trim())
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal)
                     .ToList();
        return result;
    }

    private const string ModeConflictReasonCode = "LIBRARY_INGESTION_MODE_CONFLICT";
    private const string ModeConflictDetail =
        "The library identifier is already owned by web ingestion or another lifecycle operation.";
    private const string ModeLeaseLostDetail = "The directory registration no longer owns its ingestion-mode lease.";
}
