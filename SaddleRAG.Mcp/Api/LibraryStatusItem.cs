// LibraryStatusItem.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial

namespace SaddleRAG.Mcp.Api;

/// <summary>One indexed library in a <see cref="StatusResponse" />.</summary>
public sealed record LibraryStatusItem(string Name, string Version, string Health);
