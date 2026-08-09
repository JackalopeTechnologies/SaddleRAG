// ServiceHealthResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp;

/// <summary>HTTP health response that preserves core health while reporting optional capabilities.</summary>
public sealed record ServiceHealthResponse(string Status,
                                           string WarmupStatus,
                                           string WarmupPhase,
                                           string? WarmupError,
                                           DocumentIngestionHealthResponse DocumentIngestion);
