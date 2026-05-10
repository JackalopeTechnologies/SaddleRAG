// LibraryStatusItem.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

namespace SaddleRAG.Mcp.Api;

/// <summary>One indexed library in a <see cref="StatusResponse" />.</summary>
/// <param name="Name">Human-readable library name.</param>
/// <param name="Version">Latest ingested version string.</param>
/// <param name="Health">Library health status; currently always "Healthy".</param>
public sealed record LibraryStatusItem(string Name, string Version, string Health);
