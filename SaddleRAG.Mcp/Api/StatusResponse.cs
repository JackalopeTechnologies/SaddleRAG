// StatusResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

namespace SaddleRAG.Mcp.Api;

/// <summary>
///     Lightweight status snapshot served at <c>GET /api/status</c>.
///     Consumed by the VS Code extension sidebar poller.
/// </summary>
public sealed record StatusResponse(
    IReadOnlyList<LibraryStatusItem> Libraries,
    IReadOnlyList<ActiveJobItem> ActiveJobs);
