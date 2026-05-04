// QuerySample.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Monitor;

public sealed record QuerySample
{
    public required DateTime At { get; init; }
    public required string Operation { get; init; }
    public required double DurationMs { get; init; }
    public required bool Success { get; init; }
    public int? ResultCount { get; init; }
    public string? Note { get; init; }
}
