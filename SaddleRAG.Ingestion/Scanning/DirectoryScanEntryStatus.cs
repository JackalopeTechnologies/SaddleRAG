// DirectoryScanEntryStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Outcome for one item encountered during a directory preview.</summary>
public enum DirectoryScanEntryStatus
{
    Extracted = 0,
    Skipped = 1,
    Failed = 2
}
