// RenderModeVoter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;

#endregion

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     Samples the delta in substantial content nodes between DOMContentLoaded
///     and LoadState.Load across the first <see cref="VoteSampleSize" /> pages
///     of a crawl, then classifies the site as SSR or SPA. Once the vote is
///     complete, callers skip <c>WaitForPageAndFramesAsync</c> on SSR sites,
///     saving 4–5 seconds per page.
/// </summary>
public sealed class RenderModeVoter
{
    private const int VoteSampleSize = 5;
    private const int DeltaThreshold = 3;

    private readonly object mLock = new();
    private readonly List<int> mDeltas = new();

    /// <summary>
    ///     Number of valid samples recorded so far.
    /// </summary>
    public int SamplesRecorded
    {
        get
        {
            lock(mLock)
                return mDeltas.Count;
        }
    }

    /// <summary>
    ///     True once <see cref="VoteSampleSize" /> valid samples have been recorded.
    /// </summary>
    public bool IsVoteComplete
    {
        get
        {
            lock(mLock)
                return mDeltas.Count >= VoteSampleSize;
        }
    }

    /// <summary>
    ///     Detected render mode. <see cref="RenderMode.Unknown" /> until the
    ///     vote is complete.
    /// </summary>
    public RenderMode RenderMode
    {
        get
        {
            RenderMode res = Core.Enums.RenderMode.Unknown;

            lock(mLock)
            {
                if (mDeltas.Count >= VoteSampleSize)
                    res = Median(mDeltas) > DeltaThreshold
                        ? Core.Enums.RenderMode.SPA
                        : Core.Enums.RenderMode.SSR;
            }

            return res;
        }
    }

    /// <summary>
    ///     Whether the Load-state wait is needed. Returns <c>true</c> (safe
    ///     default) until the vote is complete, then reflects the vote result.
    /// </summary>
    public bool IsLoadWaitNeeded
    {
        get
        {
            bool res = true;

            lock(mLock)
            {
                if (mDeltas.Count >= VoteSampleSize)
                    res = Median(mDeltas) > DeltaThreshold;
            }

            return res;
        }
    }

    /// <summary>
    ///     Median content-node delta across recorded samples.
    ///     Returns -1 when the vote is not yet complete.
    /// </summary>
    public int MedianDelta
    {
        get
        {
            int res = -1;

            lock(mLock)
            {
                if (mDeltas.Count >= VoteSampleSize)
                    res = Median(mDeltas);
            }

            return res;
        }
    }

    /// <summary>
    ///     Records one page's content-node counts at DOMContentLoaded and
    ///     at LoadState.Load. Negative counts (measurement failure) are
    ///     silently ignored. Samples after the vote is complete are ignored.
    /// </summary>
    public void RecordSample(int domCount, int loadCount)
    {
        if (domCount >= 0 && loadCount >= 0)
        {
            lock(mLock)
            {
                if (mDeltas.Count < VoteSampleSize)
                    mDeltas.Add(loadCount - domCount);
            }
        }
    }

    private static int Median(List<int> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
