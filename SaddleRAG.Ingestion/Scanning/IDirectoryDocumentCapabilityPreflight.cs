// IDirectoryDocumentCapabilityPreflight.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#pragma warning disable STR0010 // Interface methods cannot validate parameters

using SaddleRAG.Core.Models;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Checks optional document capability before a manual scan is queued.</summary>
public interface IDirectoryDocumentCapabilityPreflight
{
    Task<DocumentCapabilityPreflightResult> EvaluateAsync(
        DirectoryLibraryDefinition definition,
        CancellationToken cancellationToken = default);
}
