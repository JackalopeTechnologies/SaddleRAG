// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Documents.Intake;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>
///     Exact bytes and sanitized source metadata captured by the common
///     directory scanner after its before/after stability checks pass.
/// </summary>
public sealed record DirectoryStableDocument(string NormalizedRelativePath,
                                             string DisplayRelativePath,
                                             string MediaType,
                                             DirectoryEntrySnapshot Source,
                                             ReadOnlyMemory<byte> Content)
{
    internal DocumentExtractionFingerprint? ExtractionFingerprint { get; init; }
}
