// LibraryIngestionDataEvidence.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Durable evidence used to reconcile libraries created before the source-mode fence existed.</summary>
public sealed record LibraryIngestionDataEvidence(bool HasLibraryRecord,
                                                  bool HasDirectoryDefinition,
                                                  bool HasDocumentLifecycleData,
                                                  bool HasChildContentData,
                                                  bool HasOperationalHistory)
{
    /// <summary>
    ///     Reconnaissance configuration may exist before the first web scrape creates a library.
    ///     Keep it distinct from acquired content while retaining ownership and cleanup protection.
    /// </summary>
    public bool HasLibraryProfileData { get; init; }

    public bool HasOwnedContent => HasLibraryRecord ||
                                   HasDirectoryDefinition ||
                                   HasDocumentLifecycleData ||
                                   HasChildContentData ||
                                   HasLibraryProfileData;

    public bool HasAnyData => HasOwnedContent || HasOperationalHistory;
}
