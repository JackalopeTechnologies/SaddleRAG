// DirectoryRootValidationResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Sanitized validation outcome for a selected directory root.</summary>
public sealed record DirectoryRootValidationResult(bool Succeeded,
                                                   string CanonicalRoot,
                                                   string ReasonCode,
                                                   string Detail,
                                                   DirectoryEntrySnapshot? Snapshot = null);
