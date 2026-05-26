// SuspectFlagEvent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record SuspectFlagEvent
{
    public required string JobId { get; init; }
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
}
