// PipelineRates.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Per-second pipeline rates emitted by <see cref="RatesAccumulator" />.
///     A value of <see cref="Zero" /> indicates either the first sample of a
///     run or two samples that landed at the same instant.
/// </summary>
public sealed record PipelineRates
{
    public double PagesFetchedPerSec { get; init; }
    public double PagesClassifiedPerSec { get; init; }
    public double ChunksGeneratedPerSec { get; init; }
    public double ChunksEmbeddedPerSec { get; init; }
    public double PagesCompletedPerSec { get; init; }

    public static PipelineRates Zero { get; } = new PipelineRates();
}
