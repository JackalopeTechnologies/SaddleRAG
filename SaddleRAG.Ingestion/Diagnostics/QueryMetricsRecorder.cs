// QueryMetricsRecorder.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Ingestion.Diagnostics;

/// <summary>
///     Thread-safe in-memory recorder of query latency samples since process start.
///     Bounded ring buffer; oldest samples drop when capacity is reached.
/// </summary>
public sealed class QueryMetricsRecorder : IQueryMetrics
{
    public QueryMetricsRecorder(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, other: 1);
        mCapacity = capacity;
        ProcessStartedUtc = DateTime.UtcNow;
    }

    private readonly int mCapacity;
    private readonly object mLock = new object();
    private readonly LinkedList<QuerySample> mSamples = [];

    public DateTime ProcessStartedUtc { get; }

    public void Record(string operation,
                       TimeSpan duration,
                       bool success,
                       int? resultCount = null,
                       string? note = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        var sample = new QuerySample
                         {
                             At = DateTime.UtcNow,
                             Operation = operation,
                             DurationMs = duration.TotalMilliseconds,
                             Success = success,
                             ResultCount = resultCount,
                             Note = note
                         };
        lock(mLock)
        {
            mSamples.AddLast(sample);
            while (mSamples.Count > mCapacity)
                mSamples.RemoveFirst();
        }
    }

    public QueryMetricsSnapshot Snapshot()
    {
        QuerySample[] copy;
        lock(mLock)
        {
            copy = mSamples.ToArray();
        }

        var perOp = copy.GroupBy(s => s.Operation, StringComparer.Ordinal)
                        .Select(BuildStats)
                        .OrderByDescending(s => s.Count)
                        .ToList();

        var snapshot = new QueryMetricsSnapshot
                           {
                               ProcessStartedUtc = ProcessStartedUtc,
                               RecentSamples = copy,
                               PerOperation = perOp
                           };
        return snapshot;
    }

    private static QueryOperationStats BuildStats(IGrouping<string, QuerySample> g)
    {
        var sorted = g.Select(s => s.DurationMs).OrderBy(d => d).ToArray();
        return new QueryOperationStats
                   {
                       Operation = g.Key,
                       Count = sorted.Length,
                       FailureCount = g.Count(s => !s.Success),
                       AvgMs = sorted.Average(),
                       P50Ms = Percentile(sorted, P50),
                       P95Ms = Percentile(sorted, P95),
                       MaxMs = sorted[^1]
                   };
    }

    private static double Percentile(double[] sortedAsc, double p)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sortedAsc.Length, other: 0);
        var rank = p * (sortedAsc.Length - 1);
        var lo = (int) Math.Floor(rank);
        var hi = (int) Math.Ceiling(rank);
        var fraction = rank - lo;
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * fraction;
    }

    private const int DefaultCapacity = 5000;
    private const double P50 = 0.50;
    private const double P95 = 0.95;
}
