// JobInfo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Minimal job information shown in the JobDetailPage header.
/// </summary>
public sealed record JobInfo
{
    public required string JobId { get; init; }
    public required JobType Type { get; init; }
    public required string LibraryId { get; init; }
    public required string Version { get; init; }
    public required string Status { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
