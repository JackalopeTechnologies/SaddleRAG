// HostRateLimiter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading.Channels;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Adaptive per-host concurrency limiter using AIMD
///     (additive-increase, multiplicative-decrease).
///     Starts at <c>initialConcurrency</c>, grows by 1 after every
///     <c>growthThreshold</c> consecutive successes (capped at
///     <c>maxConcurrency</c>), halves on rate-limit signals (floored at
///     <c>minConcurrency</c>), and applies a Retry-After-aware penalty
///     pause to all subsequent acquires.
/// </summary>
/// <remarks>
///     One instance per host. The crawler holds a dictionary keyed by host
///     and routes each fetch through the matching limiter.
/// </remarks>
public sealed class HostRateLimiter
{
    public HostRateLimiter(int initialConcurrency,
                           int minConcurrency,
                           int maxConcurrency,
                           int growthThreshold = DefaultGrowthThreshold,
                           TimeSpan? defaultPenalty = null)
    {
        if (minConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(minConcurrency), "minConcurrency must be >= 1");
        if (maxConcurrency < minConcurrency)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "maxConcurrency must be >= minConcurrency");
        if (initialConcurrency < minConcurrency || initialConcurrency > maxConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(initialConcurrency),
                                                  "initialConcurrency must be in [minConcurrency, maxConcurrency]"
                                                 );
        }

        if (growthThreshold < 1)
            throw new ArgumentOutOfRangeException(nameof(growthThreshold), "growthThreshold must be >= 1");

        mMinConcurrency = minConcurrency;
        mMaxConcurrency = maxConcurrency;
        mGrowthThreshold = growthThreshold;
        mDefaultPenalty = defaultPenalty ?? smDefaultPenaltyDuration;
        mCurrentConcurrency = initialConcurrency;
        mPermits = Channel.CreateBounded<byte>(maxConcurrency);

        for(var i = 0; i < initialConcurrency; i++)
            mPermits.Writer.TryWrite(item: 0);
    }

    /// <summary>
    ///     Snapshot of the current allowed concurrency for this host.
    /// </summary>
    public int CurrentConcurrency
    {
        get
        {
            int result;
            lock(mLock)
            {
                result = mCurrentConcurrency;
            }

            return result;
        }
    }

    /// <summary>
    ///     Snapshot of the current penalty pause end time (UTC).
    ///     <see cref="DateTime.MinValue" /> when no penalty is active.
    /// </summary>
    public DateTime PenaltyUntilUtc => new DateTime(Interlocked.Read(ref mPenaltyUntilTicks), DateTimeKind.Utc);

    private readonly TimeSpan mDefaultPenalty;
    private readonly int mGrowthThreshold;
    private readonly Lock mLock = new Lock();
    private readonly int mMaxConcurrency;
    private readonly int mMinConcurrency;
    private readonly Channel<byte> mPermits;

    private int mConsecutiveSuccesses;
    private int mCurrentConcurrency;
    private long mPenaltyUntilTicks;
    private int mPermitDeficit;

    /// <summary>
    ///     Acquire a slot for one fetch against this host. Honors any active
    ///     penalty pause first, then waits for an available permit.
    /// </summary>
    public async Task<HostSlot> AcquireAsync(CancellationToken ct = default)
    {
        var wait = PenaltyUntilUtc - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, ct);

        await mPermits.Reader.ReadAsync(ct);
        var result = new HostSlot(this);
        return result;
    }

    /// <summary>
    ///     Report a successful fetch. After
    ///     <see cref="DefaultGrowthThreshold" /> consecutive successes,
    ///     concurrency grows by 1 (capped at <c>maxConcurrency</c>).
    /// </summary>
    public void ReportSuccess()
    {
        var grow = false;

        lock(mLock)
        {
            mConsecutiveSuccesses++;
            if (mConsecutiveSuccesses >= mGrowthThreshold && mCurrentConcurrency < mMaxConcurrency)
            {
                mCurrentConcurrency++;
                mConsecutiveSuccesses = 0;
                grow = true;
            }
        }

        if (grow)
            mPermits.Writer.TryWrite(item: 0);
    }

    /// <summary>
    ///     Report a rate-limit response (HTTP 429 / 503, or an in-scope 403).
    ///     Halves concurrency (floored at <c>minConcurrency</c>) and arms a
    ///     penalty pause for <paramref name="retryAfter" /> if supplied,
    ///     otherwise the default.
    ///     For out-of-scope 403s, callers should prefer
    ///     <see cref="HostScopeFilter.GatePrefixOf" /> instead — those signal
    ///     a gated path, not a rate problem, and pacing the host punishes
    ///     unrelated working paths on the same host.
    /// </summary>
    public void ReportRateLimited(TimeSpan? retryAfter = null)
    {
        lock(mLock)
        {
            mConsecutiveSuccesses = 0;
            int newConcurrency = Math.Max(mMinConcurrency, mCurrentConcurrency / 2);
            int toRemove = mCurrentConcurrency - newConcurrency;
            mCurrentConcurrency = newConcurrency;

            // Drop `toRemove` permits deterministically. Permits sitting in the
            // channel right now are consumed synchronously; the remainder of
            // the deficit will be absorbed by future Release calls before they
            // can write back to the channel. This replaces the older
            // fire-and-forget DrainPermitsAsync, which raced against subsequent
            // Acquires when the penalty pause was short (the race surfaced in
            // tests; production's 30s default penalty masked it).
            var drained = 0;
            while (drained < toRemove && mPermits.Reader.TryRead(out _))
                drained++;
            mPermitDeficit += toRemove - drained;
        }

        var penalty = retryAfter ?? mDefaultPenalty;
        long newUntilTicks = DateTime.UtcNow.Add(penalty).Ticks;
        long current = Interlocked.Read(ref mPenaltyUntilTicks);
        var doneCas = false;
        while (!doneCas && newUntilTicks > current)
        {
            long observed = Interlocked.CompareExchange(ref mPenaltyUntilTicks, newUntilTicks, current);
            if (observed == current)
                doneCas = true;
            else
                current = observed;
        }
    }

    /// <summary>
    ///     Report a transient (non-rate-limit) error. Resets the consecutive
    ///     success counter so we don't grow on flaky upstream behavior, but
    ///     does not shrink concurrency or arm a penalty.
    /// </summary>
    public void ReportTransientError()
    {
        lock(mLock)
        {
            mConsecutiveSuccesses = 0;
        }
    }

    internal void Release()
    {
        var absorbedByDeficit = false;
        lock(mLock)
        {
            if (mPermitDeficit > 0)
            {
                mPermitDeficit--;
                absorbedByDeficit = true;
            }
        }

        if (!absorbedByDeficit)
            mPermits.Writer.TryWrite(item: 0);
    }

    /// <summary>
    ///     Returns true if <paramref name="httpStatus"/> should trigger AIMD halving
    ///     and a penalty pause. The built-in defaults (429, 503) always apply;
    ///     <paramref name="additionalStatusCodes"/> extends that set for site-specific
    ///     signals (e.g. 502 for Infragistics CDN, 520–522 for Cloudflare rate walls).
    /// </summary>
    public static bool IsRateLimitStatus(int httpStatus, IReadOnlyList<int>? additionalStatusCodes = null)
    {
        bool res = httpStatus switch
            {
                HttpTooManyRequests => true,
                HttpServiceUnavailable => true,
                var _ => false
            };
        if (!res && additionalStatusCodes != null)
            res = additionalStatusCodes.Contains(httpStatus);
        return res;
    }

    /// <summary>
    ///     HTTP 403 (Forbidden). Edge WAFs (Cloudflare, Akamai, AWS WAF)
    ///     return 403 when they classify a fetcher as a bot, but on
    ///     path-segregated WAFs (e.g. mongodb.com gates marketing while
    ///     leaving docs open) the right response is to gate the path
    ///     prefix via <see cref="HostScopeFilter" />, not pace the host.
    ///     Caller decides based on whether the URL is in the crawl's
    ///     root scope.
    /// </summary>
    public static bool IsForbiddenStatus(int httpStatus) => httpStatus == HttpForbidden;

    public const int DefaultGrowthThreshold = 25;
    private const int HttpForbidden = 403;
    private const int HttpTooManyRequests = 429;
    private const int HttpServiceUnavailable = 503;
    private static readonly TimeSpan smDefaultPenaltyDuration = TimeSpan.FromSeconds(seconds: 30);
}
