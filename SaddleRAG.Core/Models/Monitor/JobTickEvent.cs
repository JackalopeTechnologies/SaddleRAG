// JobTickEvent.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Monitor;

public sealed record JobTickEvent
{
    public required string              JobId          { get; init; }
    public required DateTime            At             { get; init; }
    public required PipelineCounters    Counters       { get; init; }
    public string?                      CurrentHost    { get; init; }
    public IReadOnlyList<RecentFetch>   RecentFetches  { get; init; } = [];
    public IReadOnlyList<RecentReject>  RecentRejects  { get; init; } = [];
    public IReadOnlyList<RecentError>   ErrorsThisTick { get; init; } = [];
}
