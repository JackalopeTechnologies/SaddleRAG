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
                                               TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        ArgumentNullException.ThrowIfNull(rootValidator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        mRepositoryFactory = repositoryFactory;
        mRootValidator = rootValidator;
        mTimeProvider = timeProvider;
    }

    private readonly RepositoryFactory mRepositoryFactory;
    private readonly DirectoryRootValidator mRootValidator;
    private readonly TimeProvider mTimeProvider;

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
            var definition = CreateDefinition(request, validation.CanonicalRoot);
            var sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
            await sources.RegisterDirectoryDefinitionAsync(definition, ct);
            result = new DirectoryRegistrationResult(DirectoryRegistrationStatuses.Registered,
                                                     request.LibraryId);
        }

        return result;
    }

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
}
