// RescrapeRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp.Api;

/// <summary>
///     Body for POST /api/monitor/libraries/{libraryId}/rescrape.
/// </summary>
public sealed record RescrapeRequest(string Version);
