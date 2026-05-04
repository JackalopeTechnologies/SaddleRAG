// QueryMetricsSnapshot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models.Monitor;

public sealed record QueryMetricsSnapshot
{
    public required DateTime ProcessStartedUtc { get; init; }
    public required IReadOnlyList<QuerySample> RecentSamples { get; init; }
    public required IReadOnlyList<QueryOperationStats> PerOperation { get; init; }
}
