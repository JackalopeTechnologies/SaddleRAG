// DirectoryScanQueueStatuses.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Stable machine-readable manual-scan queue outcomes.</summary>
public static class DirectoryScanQueueStatuses
{
    public const string Queued = "QUEUED";
    public const string Failed = "FAILED";
}
