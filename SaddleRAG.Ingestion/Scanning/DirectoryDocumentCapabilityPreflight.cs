// DirectoryDocumentCapabilityPreflight.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Documents.Docling;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Finds document formats from filesystem metadata before checking Docling.</summary>
public sealed class DirectoryDocumentCapabilityPreflight : IDirectoryDocumentCapabilityPreflight
{
    public DirectoryDocumentCapabilityPreflight(IDirectoryScanFileSystem fileSystem,
                                                IDoclingCapabilityService capability)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(capability);
        mFileSystem = fileSystem;
        mCapability = capability;
    }

    private readonly IDoclingCapabilityService mCapability;
    private readonly IDirectoryScanFileSystem mFileSystem;

    public async Task<DocumentCapabilityPreflightResult> EvaluateAsync(
        DirectoryLibraryDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        bool requiresDocling = ContainsDoclingDocument(definition, cancellationToken);
        DocumentCapabilityPreflightResult result;
        if (requiresDocling)
        {
            DoclingCapabilityStatus status = await mCapability.GetStatusAsync(
                                                 refresh: true,
                                                 cancellationToken);
            result = status.State == DoclingCapabilityState.Ready
                ? DocumentCapabilityPreflightResult.PermitWithDocling(status)
                : DocumentCapabilityPreflightResult.Blocked(status,
                                                            DoclingInstallInstructions.OfficialInstallUrl,
                                                            DoclingInstallInstructions.OfficialReleaseUrl);
        }
        else
        {
            result = DocumentCapabilityPreflightResult.PermitWithoutDocling();
        }

        return result;
    }

    private bool ContainsDoclingDocument(DirectoryLibraryDefinition definition,
                                         CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(definition.RootPath);
        var result = false;
        long directoryCount = 0;
        long entryCount = 0;
        while (pending.Count > 0 && !result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            directoryCount++;
            EnsureWithinLimit(directoryCount,
                              DirectoryScanLimits.DefaultMaxDirectoryCount,
                              DirectoryScanReasonCodes.DirectoryCountLimitExceeded,
                              DirectoryCountLimitExceededDetail);
            DirectoryEnumerationResult enumeration = mFileSystem.EnumerateDirectory(directory);
            if (enumeration.Succeeded)
            {
                try
                {
                    result = InspectEntries(definition,
                                            enumeration.Entries,
                                            pending,
                                            cancellationToken,
                                            ref entryCount);
                }
                catch(Exception error) when (IsExpectedEnumerationFailure(error))
                {
                    // A directory that disappears or becomes inaccessible is ignored by capability preflight.
                }
            }
        }

        return result;
    }

    private static bool InspectEntries(DirectoryLibraryDefinition definition,
                                       IEnumerable<DirectoryEntrySnapshot> entries,
                                       Stack<string> pending,
                                       CancellationToken cancellationToken,
                                       ref long entryCount)
    {
        var result = false;
        foreach (DirectoryEntrySnapshot entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;
            EnsureWithinLimit(entryCount,
                              DirectoryScanLimits.DefaultMaxEntryCount,
                              DirectoryScanReasonCodes.EntryCountLimitExceeded,
                              EntryCountLimitExceededDetail);
            if (IsIncluded(definition, entry))
            {
                bool directory = entry.Attributes.HasFlag(FileAttributes.Directory);
                if (directory && definition.Recursive)
                    pending.Push(entry.FullPath);
                if (!directory)
                    result = RequiresDocling(definition, entry.FullPath);
            }

            if (result)
                break;
        }

        return result;
    }

    private static void EnsureWithinLimit(long actual,
                                          long maximum,
                                          string reasonCode,
                                          string detail)
    {
        if (actual > maximum)
            throw new DirectoryIngestionException(reasonCode, detail);
    }

    private static bool IsExpectedEnumerationFailure(Exception error) =>
        error is UnauthorizedAccessException
            or System.Security.SecurityException
            or IOException;

    private static bool IsIncluded(DirectoryLibraryDefinition definition, DirectoryEntrySnapshot entry)
    {
        bool reparsePoint = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
        string relativePath = Path.GetRelativePath(definition.RootPath, entry.FullPath)
                                  .Replace('\\', '/');
        bool excluded = definition.ExclusionPatterns.Any(pattern =>
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern.Replace('\\', '/'),
                                                                          relativePath,
                                                                          ignoreCase: true));
        return !reparsePoint && !excluded;
    }

    private static bool RequiresDocling(DirectoryLibraryDefinition definition, string fullPath)
    {
        string extension = Path.GetExtension(fullPath);
        bool doclingExtension = extension.Equals(DirectoryScanLimits.PdfExtension,
                                                 StringComparison.OrdinalIgnoreCase)
                                || extension.Equals(DirectoryScanLimits.DocxExtension,
                                                    StringComparison.OrdinalIgnoreCase);
        bool allowed = definition.AllowedExtensions.Count == 0
                       || definition.AllowedExtensions.Any(candidate =>
                           NormalizeExtension(candidate).Equals(extension,
                                                                StringComparison.OrdinalIgnoreCase));
        return doclingExtension && allowed;
    }

    private static string NormalizeExtension(string extension)
    {
        string trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
    }

    private const string DirectoryCountLimitExceededDetail =
        "The directory capability preflight exceeds its configured directory-count limit.";
    private const string EntryCountLimitExceededDetail =
        "The directory capability preflight exceeds its configured entry-count limit.";
}
