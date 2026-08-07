// DirectoryEnumerationResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Typed result of enumerating one directory without recursion.</summary>
public sealed record DirectoryEnumerationResult(IReadOnlyList<DirectoryEntrySnapshot> Entries,
                                                string ReasonCode,
                                                Exception? Error)
{
    public bool Succeeded => Error == null && string.IsNullOrEmpty(ReasonCode);
}
