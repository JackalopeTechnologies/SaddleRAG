// PublishedVersionRefusal.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;

namespace SaddleRAG.Mcp.Tools;

/// <summary>Explains why a full scrape cannot replace an already published target.</summary>
internal static class PublishedVersionRefusal
{
    private const string Message = "Full scraping cannot overwrite a published version. Use a new version identifier for the next scrape. force only cleans an unpublished partial attempt; it does not bypass publication protection.";
    private const string RefusedStatus = "Refused";
    private const string PublishedVersionReason = "PUBLISHED_VERSION";

    public static string Create(string library, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return JsonSerializer.Serialize(new
                                            {
                                                Status = RefusedStatus,
                                                Reason = PublishedVersionReason,
                                                LibraryId = library,
                                                Version = version,
                                                Message
                                            });
    }

    public static async Task EnsureUnpublishedAsync(ILibraryRepository libraries,
                                                     string library,
                                                     string version,
                                                     CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentException.ThrowIfNullOrEmpty(library);
        ArgumentException.ThrowIfNullOrEmpty(version);
        var existing = await libraries.GetVersionAsync(library, version, ct);
        if (existing?.PublicationState == VersionPublicationState.Published)
            throw new InvalidOperationException(Message);
    }
}
