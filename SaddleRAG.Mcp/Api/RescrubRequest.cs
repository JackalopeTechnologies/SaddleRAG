// RescrubRequest.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Mcp.Api;

/// <summary>
///     Body for POST /api/monitor/libraries/{libraryId}/rescrub.
/// </summary>
public sealed record RescrubRequest(string Version);
