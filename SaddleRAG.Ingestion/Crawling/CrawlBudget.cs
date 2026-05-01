// CrawlBudget.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Collections.Concurrent;
using System.Globalization;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Per-crawl budget that holds one <see cref="HostRateLimiter"/> per
///     <c>(scheme, host)</c> bucket encountered during a crawl. The first
///     time a bucket is seen, a limiter is created lazily with the
///     configured initial / min / max concurrency. Bucketing on scheme +
///     host (rather than host alone) keeps file:// URLs — which have an
///     empty host — in their own bucket instead of colliding with empty
///     hosts that might come from other schemes.
/// </summary>
public sealed class CrawlBudget
{
    public CrawlBudget(int initialConcurrency = DefaultInitialConcurrency,
                       int minConcurrency = DefaultMinConcurrency,
                       int maxConcurrency = DefaultMaxConcurrency)
    {
        if (initialConcurrency < minConcurrency || initialConcurrency > maxConcurrency)
            throw new ArgumentOutOfRangeException(nameof(initialConcurrency));

        mInitialConcurrency = initialConcurrency;
        mMinConcurrency = minConcurrency;
        mMaxConcurrency = maxConcurrency;
        mLimiters = new ConcurrentDictionary<string, HostRateLimiter>(StringComparer.OrdinalIgnoreCase);
        mScopeFilters = new ConcurrentDictionary<string, HostScopeFilter>(StringComparer.OrdinalIgnoreCase);
    }

    private readonly int mInitialConcurrency;
    private readonly ConcurrentDictionary<string, HostRateLimiter> mLimiters;
    private readonly ConcurrentDictionary<string, HostScopeFilter> mScopeFilters;
    private readonly int mMaxConcurrency;
    private readonly int mMinConcurrency;

    /// <summary>
    ///     Total number of distinct buckets routed through this budget.
    ///     Stable across the crawl lifetime.
    /// </summary>
    public int HostCount => mLimiters.Count;

    /// <summary>
    ///     Build the dictionary key for a URL: <c>scheme://host</c>.
    ///     Uniform between limiters, scope filters, and reporting.
    /// </summary>
    public static string BuildHostKey(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string result = $"{uri.Scheme}://{uri.Host}";
        return result;
    }

    /// <summary>
    ///     Get (or lazily create) the limiter for a URL's <c>(scheme, host)</c>.
    /// </summary>
    public HostRateLimiter GetLimiter(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string key = BuildHostKey(uri);
        var result = mLimiters.GetOrAdd(key,
                                        _ => new HostRateLimiter(mInitialConcurrency,
                                                                 mMinConcurrency,
                                                                 mMaxConcurrency
                                                                )
                                       );
        return result;
    }

    /// <summary>
    ///     Get (or lazily create) the scope filter for a URL's <c>(scheme, host)</c>.
    ///     The filter accumulates gated path prefixes seen via 403 responses
    ///     on out-of-scope URLs and is consulted at dequeue time to drop
    ///     known-bad URLs without re-fetching them.
    /// </summary>
    public HostScopeFilter GetScopeFilter(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string key = BuildHostKey(uri);
        var result = mScopeFilters.GetOrAdd(key, _ => new HostScopeFilter());
        return result;
    }

    /// <summary>
    ///     Snapshot of all known buckets and their current concurrency.
    ///     Useful for logging or status reporting mid-crawl. Keys are
    ///     <c>scheme://host</c> strings.
    /// </summary>
    public IReadOnlyDictionary<string, int> GetSnapshot()
    {
        var result = new Dictionary<string, int>(mLimiters.Count, StringComparer.OrdinalIgnoreCase);
        foreach((string key, HostRateLimiter limiter) in mLimiters)
            result[key] = limiter.CurrentConcurrency;
        return result;
    }

    /// <summary>
    ///     Parse an HTTP <c>Retry-After</c> header value. Supports both the
    ///     delta-seconds form (e.g. <c>"30"</c>) and the HTTP-date form
    ///     (e.g. <c>"Wed, 21 Oct 2026 07:28:00 GMT"</c>). Returns null when
    ///     the input is missing, malformed, or non-positive.
    /// </summary>
    public static TimeSpan? ParseRetryAfter(string? headerValue)
    {
        TimeSpan? result = null;
        if (!string.IsNullOrWhiteSpace(headerValue))
        {
            string trimmed = headerValue.Trim();
            result = TryParseRetryAfterSeconds(trimmed) ?? TryParseRetryAfterDate(trimmed);
        }

        return result;
    }

    private static TimeSpan? TryParseRetryAfterSeconds(string value)
    {
        TimeSpan? result = null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) && seconds > 0)
            result = TimeSpan.FromSeconds(seconds);
        return result;
    }

    private static TimeSpan? TryParseRetryAfterDate(string value)
    {
        TimeSpan? result = null;
        bool parsed = DateTime.TryParse(value,
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                        out DateTime when
                                       );
        if (parsed)
        {
            var delta = when - DateTime.UtcNow;
            if (delta > TimeSpan.Zero)
                result = delta;
        }

        return result;
    }

    public const int DefaultInitialConcurrency = 4;
    public const int DefaultMinConcurrency = 1;
    public const int DefaultMaxConcurrency = 16;
}
