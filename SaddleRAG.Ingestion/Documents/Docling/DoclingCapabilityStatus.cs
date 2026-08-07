// DoclingCapabilityStatus.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>Capability status returned to health and MCP integration layers.</summary>
public sealed record DoclingCapabilityStatus(DoclingCapabilityState State,
                                             string ReasonCode,
                                             string Detail,
                                             string Endpoint,
                                             DateTimeOffset LastCheckedAt,
                                             string Remediation);
