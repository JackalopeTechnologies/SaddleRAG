// RecentFetch.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record RecentFetch
{
    public required string Url { get; init; }
    public DateTime At { get; init; }
}
