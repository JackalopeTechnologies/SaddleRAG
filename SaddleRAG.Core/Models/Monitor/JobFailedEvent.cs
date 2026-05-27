// JobFailedEvent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobFailedEvent
{
    public required string JobId { get; init; }
    public required string ErrorMessage { get; init; }
}
