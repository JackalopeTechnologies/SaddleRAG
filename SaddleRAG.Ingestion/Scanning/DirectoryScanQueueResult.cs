// DirectoryScanQueueResult.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Immediate outcome of one explicit manual-scan queue request.</summary>
public sealed record DirectoryScanQueueResult(string Status,
                                              string LibraryId,
                                              string Version,
                                              string? JobId = null,
                                              string? ReasonCode = null,
                                              string? Detail = null,
                                              string? OfficialInstallUrl = null,
                                              string? OfficialReleaseUrl = null);
