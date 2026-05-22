// DryRunAccumulator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Collections.ObjectModel;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Crawling;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Thread-safe in-memory bucket the dry-run pipeline writes into
///     instead of the live repositories. The crawl stage's parallel
///     workers (<see cref="PageCrawler" />'s worker pool) all call
///     Record* concurrently; the classify, chunk, and embed stages are
///     each single-consumer so they call from one thread, but they
///     share the accumulator with the crawl stage so every mutator
///     still locks.
///     <para>
///         Callers consume <see cref="Snapshot" /> only after the pipeline
///         has drained; the snapshot returns isolated copies of the
///         internal collections exposed through read-only interfaces,
///         safe to read without synchronization.
///     </para>
/// </summary>
public sealed class DryRunAccumulator
{
    public DryRunAccumulator(int samplePageLimit = DefaultSamplePageLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(samplePageLimit);
        mSamplePageLimit = samplePageLimit;
    }

    private readonly Lock mLock = new();
    private readonly int mSamplePageLimit;

    private readonly Dictionary<string, int> mPagesByHost = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, int> mDepthDistribution = new();
    private readonly HashSet<string> mGitHubRepos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DocCategory, int> mCategoryHistogram = new();
    private readonly List<DryRunPageEntry> mSamplePages = new();
    private readonly List<DryRunFetchError> mErrors = new();

    private int mTotalPages;
    private int mInScopePages;
    private int mOutOfScopePages;
    private int mDepthLimitedSkips;
    private int mFilteredSkips;
    private int mFetchErrors;

    private long mTotalFetchMs;
    private int mFetchSampleCount;
    private long mTotalClassifyMs;
    private int mClassifySampleCount;
    private long mTotalChunkMs;
    private int mChunkSampleCount;
    private long mTotalEmbedMs;
    private int mEmbedBatchCount;

    private RenderMode mRenderMode = RenderMode.Unknown;
    private int mMedianContentNodeDelta = -1;
    private bool mLoadWaitRecommended = true;

    private NavigatorEscalation? mEscalation;

    internal void RecordTotalPage(string hostKey, int depth, bool inScope)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostKey);

        lock(mLock)
        {
            mTotalPages++;
            if (inScope)
                mInScopePages++;
            else
                mOutOfScopePages++;
            mPagesByHost.TryGetValue(hostKey, out int hostCount);
            mPagesByHost[hostKey] = hostCount + 1;
            mDepthDistribution.TryGetValue(depth, out int existing);
            mDepthDistribution[depth] = existing + 1;
        }
    }

    internal void RecordFilteredSkip()
    {
        lock(mLock)
            mFilteredSkips++;
    }

    internal void RecordDepthLimitedSkip()
    {
        lock(mLock)
            mDepthLimitedSkips++;
    }

    internal void RecordGitHubRepo(string ownerSlashRepo)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerSlashRepo);

        lock(mLock)
            mGitHubRepos.Add(ownerSlashRepo);
    }

    internal void RecordFetchError(DryRunFetchError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        lock(mLock)
        {
            mFetchErrors++;
            mErrors.Add(error);
        }
    }

    internal void RecordFetchMs(long ms)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ms);

        lock(mLock)
        {
            mTotalFetchMs += ms;
            mFetchSampleCount++;
        }
    }

    internal void RecordSamplePage(DryRunPageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock(mLock)
        {
            if (mSamplePages.Count < mSamplePageLimit)
                mSamplePages.Add(entry);
        }
    }

    internal void RecordClassified(DocCategory category, long classifyMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(classifyMs);

        lock(mLock)
        {
            mCategoryHistogram.TryGetValue(category, out int existing);
            mCategoryHistogram[category] = existing + 1;
            mTotalClassifyMs += classifyMs;
            mClassifySampleCount++;
        }
    }

    internal void RecordChunked(long chunkMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkMs);

        lock(mLock)
        {
            mTotalChunkMs += chunkMs;
            mChunkSampleCount++;
        }
    }

    internal void RecordEmbeddedBatch(long embedMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(embedMs);

        lock(mLock)
        {
            mTotalEmbedMs += embedMs;
            mEmbedBatchCount++;
        }
    }

    internal void RecordRenderMode(RenderMode mode, int medianContentNodeDelta, bool loadWaitRecommended)
    {
        lock(mLock)
        {
            mRenderMode = mode;
            mMedianContentNodeDelta = medianContentNodeDelta;
            mLoadWaitRecommended = loadWaitRecommended;
        }
    }

    /// <summary>
    ///     Records that the crawler swapped to the SPA navigator with the
    ///     given human-readable reason (framework signal or
    ///     "user-supplied waitForSelector").
    /// </summary>
    internal void RecordNavigatorSwap(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);

        lock(mLock)
            mEscalation = new NavigatorEscalation { Reason = reason };
    }

    public DryRunSnapshot Snapshot()
    {
        DryRunSnapshot result;
        lock(mLock)
        {
            result = new DryRunSnapshot
                         {
                             TotalPages = mTotalPages,
                             InScopePages = mInScopePages,
                             OutOfScopePages = mOutOfScopePages,
                             DepthLimitedSkips = mDepthLimitedSkips,
                             FilteredSkips = mFilteredSkips,
                             FetchErrors = mFetchErrors,
                             PagesByHost = new ReadOnlyDictionary<string, int>(
                                 new Dictionary<string, int>(mPagesByHost, StringComparer.OrdinalIgnoreCase)),
                             DepthDistribution = new ReadOnlyDictionary<int, int>(
                                 new Dictionary<int, int>(mDepthDistribution)),
                             GitHubRepos = mGitHubRepos.OrderBy(r => r).ToList().AsReadOnly(),
                             CategoryHistogram = new ReadOnlyDictionary<DocCategory, int>(
                                 new Dictionary<DocCategory, int>(mCategoryHistogram)),
                             SamplePages = mSamplePages.ToList().AsReadOnly(),
                             Errors = mErrors.ToList().AsReadOnly(),
                             RenderMode = mRenderMode,
                             MedianContentNodeDelta = mMedianContentNodeDelta,
                             LoadWaitRecommended = mLoadWaitRecommended,
                             Timings = new StageTimings
                                           {
                                               TotalFetchMs = mTotalFetchMs,
                                               FetchSampleCount = mFetchSampleCount,
                                               TotalClassifyMs = mTotalClassifyMs,
                                               ClassifySampleCount = mClassifySampleCount,
                                               TotalChunkMs = mTotalChunkMs,
                                               ChunkSampleCount = mChunkSampleCount,
                                               TotalEmbedMs = mTotalEmbedMs,
                                               EmbedBatchCount = mEmbedBatchCount
                                           },
                             Escalation = mEscalation
                         };
        }

        return result;
    }

    private const int DefaultSamplePageLimit = 50;
}
