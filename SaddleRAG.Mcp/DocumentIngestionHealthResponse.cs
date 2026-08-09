// DocumentIngestionHealthResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp;

/// <summary>Cached health detail for the optional document-ingestion capability.</summary>
public sealed record DocumentIngestionHealthResponse(string Status,
                                                     string ReasonCode,
                                                     string Detail,
                                                     string Endpoint,
                                                     DateTimeOffset LastCheckedAt,
                                                     string Remediation);
