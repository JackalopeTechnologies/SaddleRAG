// QueryOperationStats.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Monitor;

public sealed record QueryOperationStats
{
    public required string Operation { get; init; }
    public required int Count { get; init; }
    public required int FailureCount { get; init; }
    public required double AvgMs { get; init; }
    public required double P50Ms { get; init; }
    public required double P95Ms { get; init; }
    public required double MaxMs { get; init; }
}
