// DirectoryScanStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Overall result of one explicit directory preview.</summary>
public enum DirectoryScanStatus
{
    Completed = 0,
    CompletedWithErrors = 1,
    Failed = 2
}
