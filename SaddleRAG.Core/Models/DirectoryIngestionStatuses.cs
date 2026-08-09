// DirectoryIngestionStatuses.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Stable top-level statuses returned by directory ingestion.</summary>
public static class DirectoryIngestionStatuses
{
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
}
