// DirectoryPathResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Typed result of inspecting one filesystem path.</summary>
public sealed record DirectoryPathResult(DirectoryEntrySnapshot? Snapshot,
                                         string ReasonCode,
                                         Exception? Error)
{
    public bool Succeeded => Snapshot != null && string.IsNullOrEmpty(ReasonCode);
}
