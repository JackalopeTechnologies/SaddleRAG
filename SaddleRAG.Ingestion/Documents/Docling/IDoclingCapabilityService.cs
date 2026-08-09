// IDoclingCapabilityService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     Cached capability boundary used by startup, health, and later manual status checks.
/// </summary>
public interface IDoclingCapabilityService
{
    DoclingCapabilityStatus CurrentStatus { get; }

    Task<DoclingCapabilityStatus> GetStatusAsync(bool refresh = false,
                                                 CancellationToken cancellationToken = default);

    void RecordUnexpectedFailure();
}
