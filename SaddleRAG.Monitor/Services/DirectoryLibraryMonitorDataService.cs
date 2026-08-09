// DirectoryLibraryMonitorDataService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Projects registered directory definitions, published versions, and
///     manual-scan jobs for the Monitor without exposing repository details to Razor.
/// </summary>
public sealed class DirectoryLibraryMonitorDataService : IDirectoryLibraryMonitorDataService
{
    public DirectoryLibraryMonitorDataService(RepositoryFactory repositoryFactory)
    {
        ArgumentNullException.ThrowIfNull(repositoryFactory);
        mRepositoryFactory = repositoryFactory;
    }

    private readonly RepositoryFactory mRepositoryFactory;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DirectoryLibraryMonitorRow>> ListAsync(
        string? profile,
        CancellationToken ct = default)
    {
        ISourceDocumentRepository sources = mRepositoryFactory.GetSourceDocumentRepository(profile);
        ILibraryRepository libraries = mRepositoryFactory.GetLibraryRepository(profile);
        IJobRepository jobs = mRepositoryFactory.GetJobRepository(profile);
        IReadOnlyList<DirectoryLibraryDefinition> definitions = await sources.GetDirectoryDefinitionsAsync(ct);
        IReadOnlyList<JobRecord> recentJobs = await jobs.ListRecentAsync(JobType.DirectoryScan,
                                                                         RecentDirectoryJobLimit,
                                                                         ct);
        var rows = new List<DirectoryLibraryMonitorRow>(definitions.Count);
        foreach(DirectoryLibraryDefinition definition in definitions)
        {
            LibraryRecord? library = await libraries.GetLibraryAsync(definition.Id, ct);
            LibraryVersionRecord? publishedVersion = await GetPublishedVersionAsync(definition,
                                                                                      library,
                                                                                      libraries,
                                                                                      ct);
            JobRecord? latestJob = recentJobs.Where(job => string.Equals(job.LibraryId,
                                                                          definition.Id,
                                                                          StringComparison.OrdinalIgnoreCase))
                                             .OrderByDescending(job => job.CreatedAt)
                                             .FirstOrDefault();
            rows.Add(CreateRow(definition, library, publishedVersion, latestJob));
        }

        return rows.OrderBy(row => row.LibraryId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<LibraryVersionRecord?> GetPublishedVersionAsync(
        DirectoryLibraryDefinition definition,
        LibraryRecord? library,
        ILibraryRepository libraries,
        CancellationToken ct)
    {
        string? currentVersion = library?.CurrentVersion;
        string? version = string.IsNullOrWhiteSpace(currentVersion)
                              ? definition.LastPublishedVersion
                              : currentVersion;
        LibraryVersionRecord? result = null;
        if (!string.IsNullOrWhiteSpace(version))
        {
            LibraryVersionRecord? candidate = await libraries.GetVersionAsync(definition.Id, version, ct);
            if (candidate?.PublicationState == VersionPublicationState.Published)
                result = candidate;
        }

        return result;
    }

    private static DirectoryLibraryMonitorRow CreateRow(DirectoryLibraryDefinition definition,
                                                         LibraryRecord? library,
                                                         LibraryVersionRecord? publishedVersion,
                                                         JobRecord? latestJob) =>
        new()
            {
                LibraryId = definition.Id,
                Name = library?.Name ?? definition.Name ?? definition.Id,
                Hint = library?.Hint ?? definition.Hint ?? string.Empty,
                RootPath = definition.RootPath,
                Recursive = definition.Recursive,
                AllowedExtensions = definition.AllowedExtensions.ToArray(),
                ExclusionPatterns = definition.ExclusionPatterns.ToArray(),
                BindingStatus = definition.BindingStatus,
                LastSuccessfulVersion = publishedVersion?.Version ?? definition.LastPublishedVersion,
                LastSuccessfulAt = publishedVersion?.ScrapedAt ?? definition.LastPublishedAtUtc,
                LatestJobId = latestJob?.Id,
                LatestJobStatus = latestJob?.Status.ToString(),
                Progress = latestJob?.DirectoryScanProgress,
                FileFailures = SanitizeFailures(latestJob?.DirectoryScanFailures, definition.RootPath)
            };

    private static IReadOnlyList<DirectoryScanFileFailure> SanitizeFailures(
        IReadOnlyList<DirectoryScanFileFailure>? failures,
        string rootPath)
    {
        var result = (failures ?? [])
                     .Select(failure => new DirectoryScanFileFailure(
                                 SanitizeRelativePath(failure.RelativePath),
                                 failure.ReasonCode,
                                 SanitizeDetail(failure.Detail, rootPath)))
                     .ToList();
        return result;
    }

    private static string SanitizeRelativePath(string relativePath)
    {
        string candidate = relativePath.Replace('\\', '/');
        bool invalid = string.IsNullOrWhiteSpace(candidate)
                       || Path.IsPathRooted(candidate)
                       || candidate.Split('/', StringSplitOptions.RemoveEmptyEntries)
                                   .Any(segment => segment == ParentDirectorySegment);
        string result = invalid ? InvalidRelativePathDisplay : candidate.TrimStart('/');
        return result;
    }

    private static string SanitizeDetail(string detail, string rootPath)
    {
        string result = detail;
        if (!string.IsNullOrWhiteSpace(rootPath))
        {
            result = result.Replace(rootPath,
                                    RegisteredRootDisplay,
                                    StringComparison.OrdinalIgnoreCase);
            string alternateRoot = rootPath.Replace('\\', '/');
            result = result.Replace(alternateRoot,
                                    RegisteredRootDisplay,
                                    StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private const int RecentDirectoryJobLimit = 100;
    private const string ParentDirectorySegment = "..";
    private const string InvalidRelativePathDisplay = "(invalid relative path)";
    private const string RegisteredRootDisplay = "[registered root]";
}
