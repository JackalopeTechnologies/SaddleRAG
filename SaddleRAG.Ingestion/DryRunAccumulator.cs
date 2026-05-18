// DryRunAccumulator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Thread-safe in-memory bucket the dry-run pipeline writes into
///     instead of the live repositories. The crawl stage's 8 parallel
///     workers all call Record* concurrently; the classify, chunk, and
///     embed stages are each single-consumer so they call from one
///     thread, but they share the accumulator with the crawl stage so
///     every mutator still locks.
///     <para>
///         Callers consume <see cref="Snapshot" /> only after the pipeline
///         has drained; the snapshot returns immutable copies of the
///         internal collections.
///     </para>
/// </summary>
internal sealed class DryRunAccumulator
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

    public void RecordTotalPage(string hostKey, int depth, bool inScope)
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

    public void RecordFilteredSkip()
    {
        lock(mLock)
            mFilteredSkips++;
    }

    public void RecordDepthLimitedSkip()
    {
        lock(mLock)
            mDepthLimitedSkips++;
    }

    public void RecordGitHubRepo(string ownerSlashRepo)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerSlashRepo);

        lock(mLock)
            mGitHubRepos.Add(ownerSlashRepo);
    }

    public void RecordFetchError(DryRunFetchError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        lock(mLock)
        {
            mFetchErrors++;
            mErrors.Add(error);
        }
    }

    public void RecordFetchMs(long ms)
    {
        lock(mLock)
        {
            mTotalFetchMs += ms;
            mFetchSampleCount++;
        }
    }

    public void RecordSamplePage(DryRunPageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock(mLock)
        {
            if (mSamplePages.Count < mSamplePageLimit)
                mSamplePages.Add(entry);
        }
    }

    public void RecordClassified(DocCategory category, long classifyMs)
    {
        lock(mLock)
        {
            mCategoryHistogram.TryGetValue(category, out int existing);
            mCategoryHistogram[category] = existing + 1;
            mTotalClassifyMs += classifyMs;
            mClassifySampleCount++;
        }
    }

    public void RecordChunked(long chunkMs)
    {
        lock(mLock)
        {
            mTotalChunkMs += chunkMs;
            mChunkSampleCount++;
        }
    }

    public void RecordEmbeddedBatch(long embedMs)
    {
        lock(mLock)
        {
            mTotalEmbedMs += embedMs;
            mEmbedBatchCount++;
        }
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
                             PagesByHost = new Dictionary<string, int>(mPagesByHost, StringComparer.OrdinalIgnoreCase),
                             DepthDistribution = new Dictionary<int, int>(mDepthDistribution),
                             GitHubRepos = mGitHubRepos.OrderBy(r => r).ToList(),
                             CategoryHistogram = new Dictionary<DocCategory, int>(mCategoryHistogram),
                             SamplePages = mSamplePages.ToList(),
                             Errors = mErrors.ToList(),
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
                                           }
                         };
        }

        return result;
    }

    private const int DefaultSamplePageLimit = 50;
}
