// DirectoryRootValidator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Validates and canonicalizes the user-selected scan root.</summary>
public sealed class DirectoryRootValidator
{
    public DirectoryRootValidator(IDirectoryScanFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        mFileSystem = fileSystem;
    }

    private readonly IDirectoryScanFileSystem mFileSystem;

    public DirectoryRootValidationResult Validate(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        DirectoryRootValidationResult result;
        if (string.IsNullOrWhiteSpace(rootPath))
            result = Failure(DirectoryScanReasonCodes.RootPathRequired);
        else
        {
            result = Path.IsPathFullyQualified(rootPath)
                ? ValidateCanonicalRoot(rootPath)
                : Failure(DirectoryScanReasonCodes.RootPathNotAbsolute);
        }
        return result;
    }

    private DirectoryRootValidationResult ValidateCanonicalRoot(string rootPath)
    {
        DirectoryRootValidationResult result;
        try
        {
            var canonicalRoot = Path.GetFullPath(rootPath);
            var inspection = mFileSystem.InspectPath(canonicalRoot);
            result = ValidateInspection(canonicalRoot, inspection);
        }
        catch(ArgumentException)
        {
            result = Failure(DirectoryScanReasonCodes.RootPathNotAbsolute);
        }
        catch(NotSupportedException)
        {
            result = Failure(DirectoryScanReasonCodes.RootPathNotAbsolute);
        }

        return result;
    }

    private static DirectoryRootValidationResult ValidateInspection(string canonicalRoot,
                                                                    DirectoryPathResult inspection)
    {
        DirectoryRootValidationResult result;
        var snapshot = inspection.Snapshot;
        if (!inspection.Succeeded || snapshot == null)
            result = Failure(NormalizeInspectionReason(inspection.ReasonCode));
        else
        {
            if (!snapshot.Attributes.HasFlag(FileAttributes.Directory))
                result = Failure(DirectoryScanReasonCodes.RootNotDirectory);
            else
            {
                result = snapshot.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    ? Failure(DirectoryScanReasonCodes.RootReparsePointNotAllowed)
                    : new DirectoryRootValidationResult(true,
                                                        canonicalRoot,
                                                        DirectoryScanReasonCodes.ScanCompleted,
                                                        ValidRootDetail);
            }
        }
        return result;
    }

    private static string NormalizeInspectionReason(string reasonCode)
    {
        var result = reasonCode switch
            {
                DirectoryScanReasonCodes.RootAccessDenied => DirectoryScanReasonCodes.RootAccessDenied,
                DirectoryScanReasonCodes.RootNotDirectory => DirectoryScanReasonCodes.RootNotDirectory,
                _ => DirectoryScanReasonCodes.RootNotFound
            };
        return result;
    }

    private static DirectoryRootValidationResult Failure(string reasonCode) =>
        new(false, string.Empty, reasonCode, DetailFor(reasonCode));

    private static string DetailFor(string reasonCode)
    {
        var result = reasonCode switch
            {
                DirectoryScanReasonCodes.RootPathRequired => PathRequiredDetail,
                DirectoryScanReasonCodes.RootPathNotAbsolute => PathNotAbsoluteDetail,
                DirectoryScanReasonCodes.RootNotDirectory => NotDirectoryDetail,
                DirectoryScanReasonCodes.RootAccessDenied => AccessDeniedDetail,
                DirectoryScanReasonCodes.RootReparsePointNotAllowed => ReparsePointNotAllowedDetail,
                _ => NotFoundDetail
            };
        return result;
    }

    private const string ValidRootDetail = "The directory is ready for preview.";
    private const string PathRequiredDetail = "A directory path is required.";
    private const string PathNotAbsoluteDetail = "The directory path must be absolute.";
    private const string NotDirectoryDetail = "The selected path is not a directory.";
    private const string AccessDeniedDetail = "The selected directory cannot be accessed.";
    private const string ReparsePointNotAllowedDetail =
        "A linked or redirected directory cannot be used as the scan root.";
    private const string NotFoundDetail = "The selected directory was not found.";
}
