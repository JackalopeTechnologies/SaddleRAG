// QueryMetricsExtensions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Diagnostics;
using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Ingestion.Diagnostics;

/// <summary>
///     Helper extensions for timing async work against an <see cref="IQueryMetrics" />.
///     Records exactly one sample whether the work succeeds or throws, and re-throws
///     the original exception unchanged on failure.
/// </summary>
public static class QueryMetricsExtensions
{
    /// <summary>
    ///     Run <paramref name="work" />, time it, and append a single sample to
    ///     <paramref name="metrics" /> regardless of success or failure.
    ///     The original exception (if any) is re-thrown after the sample is recorded.
    /// </summary>
    /// <typeparam name="T">Result type of the async work.</typeparam>
    /// <param name="metrics">Metrics recorder. Required.</param>
    /// <param name="operation">Operation label (e.g. "embed_query"). Required.</param>
    /// <param name="work">Async unit of work to time. Required.</param>
    /// <param name="resultCount">
    ///     Optional projection from the result to a count for the sample (e.g. number of search hits).
    ///     Only invoked on success.
    /// </param>
    /// <param name="note">Optional free-form note recorded with the sample.</param>
    public static async Task<T> TimeAsync<T>(this IQueryMetrics metrics,
                                             string operation,
                                             Func<Task<T>> work,
                                             Func<T, int?>? resultCount = null,
                                             string? note = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentNullException.ThrowIfNull(work);

        var sw = Stopwatch.StartNew();
        T result;
        try
        {
            result = await work();
        }
        catch
        {
            sw.Stop();
            metrics.Record(operation, sw.Elapsed, success: false, resultCount: null, note);
            throw;
        }

        sw.Stop();
        metrics.Record(operation, sw.Elapsed, success: true, resultCount?.Invoke(result), note);
        return result;
    }
}
