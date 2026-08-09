// StableFileReadResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Immutable bytes plus the metadata captured before and after reading.</summary>
public sealed record StableFileReadResult(ReadOnlyMemory<byte> Content,
                                          DirectoryEntrySnapshot? Before,
                                          DirectoryEntrySnapshot? After,
                                          string ReasonCode,
                                          Exception? Error)
{
    public bool Succeeded => Error == null
                             && Before != null
                             && After != null
                             && string.IsNullOrEmpty(ReasonCode);
}
