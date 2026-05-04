// RatesAccumulator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Holds the last <see cref="PipelineCounters" /> sample and emits per-second
///     rates from the delta against the next sample. Two-sample sliding window
///     so transient stalls are visible immediately rather than smoothed by a
///     longer history.
/// </summary>
public sealed class RatesAccumulator
{
    private (DateTime At, PipelineCounters Counters)? mLastSample;

    public PipelineRates Update(PipelineCounters now, DateTime sampleAt)
    {
        ArgumentNullException.ThrowIfNull(now);
        var rates = PipelineRates.Zero;
        if (mLastSample is { } prior)
        {
            var elapsed = (sampleAt - prior.At).TotalSeconds;
            if (elapsed > 0)
            {
                rates = new PipelineRates
                            {
                                PagesFetchedPerSec    = (now.PagesFetched    - prior.Counters.PagesFetched)    / elapsed,
                                PagesClassifiedPerSec = (now.PagesClassified - prior.Counters.PagesClassified) / elapsed,
                                ChunksGeneratedPerSec = (now.ChunksGenerated - prior.Counters.ChunksGenerated) / elapsed,
                                ChunksEmbeddedPerSec  = (now.ChunksEmbedded  - prior.Counters.ChunksEmbedded)  / elapsed,
                                PagesCompletedPerSec  = (now.PagesCompleted  - prior.Counters.PagesCompleted)  / elapsed
                            };
            }
        }

        mLastSample = (sampleAt, now);
        return rates;
    }
}
