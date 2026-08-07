// DirectoryScanEntryResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Sanitized deterministic result for one path beneath the selected root.</summary>
public sealed record DirectoryScanEntryResult(string RelativePath,
                                              DirectoryScanEntryKind Kind,
                                              DirectoryScanEntryStatus Status,
                                              string ReasonCode,
                                              string Detail,
                                              int SectionCount,
                                              long ByteLength);
