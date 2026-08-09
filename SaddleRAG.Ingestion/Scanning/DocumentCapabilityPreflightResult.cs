// DocumentCapabilityPreflightResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Documents.Docling;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Immediate capability decision made before a manual scan creates durable state.</summary>
public sealed record DocumentCapabilityPreflightResult(bool Allowed,
                                                       bool RequiresDocling,
                                                       DoclingCapabilityStatus? Status,
                                                       string? OfficialInstallUrl,
                                                       string? OfficialReleaseUrl)
{
    public static DocumentCapabilityPreflightResult PermitWithoutDocling() =>
        new(true, false, null, null, null);

    public static DocumentCapabilityPreflightResult PermitWithDocling(DoclingCapabilityStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new DocumentCapabilityPreflightResult(true, true, status, null, null);
    }

    public static DocumentCapabilityPreflightResult Blocked(DoclingCapabilityStatus status,
                                                            string officialInstallUrl,
                                                            string officialReleaseUrl)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrEmpty(officialInstallUrl);
        ArgumentException.ThrowIfNullOrEmpty(officialReleaseUrl);
        return new DocumentCapabilityPreflightResult(false,
                                                     true,
                                                     status,
                                                     officialInstallUrl,
                                                     officialReleaseUrl);
    }
}
