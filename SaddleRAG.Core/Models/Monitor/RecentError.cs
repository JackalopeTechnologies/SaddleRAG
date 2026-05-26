// RecentError.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record RecentError
{
    public required string Message { get; init; }
    public string? Url { get; init; }
    public DateTime At { get; init; }
}
