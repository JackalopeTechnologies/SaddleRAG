// RescrubRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Mcp.Api;

/// <summary>
///     Body for POST /api/monitor/libraries/{libraryId}/rescrub.
/// </summary>
public sealed record RescrubRequest(string Version);
