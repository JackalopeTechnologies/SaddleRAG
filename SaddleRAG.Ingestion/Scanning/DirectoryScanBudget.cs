// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Tracks aggregate extraction output accepted by one directory scan.</summary>
internal sealed class DirectoryScanBudget
{
    internal DirectoryScanBudget(long maxTotalBytes, int maxSectionCount)
    {
        if (maxTotalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));
        if (maxSectionCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSectionCount));
        mMaxTotalBytes = maxTotalBytes;
        mMaxSectionCount = maxSectionCount;
    }

    private readonly long mMaxTotalBytes;
    private readonly int mMaxSectionCount;
    private int mSectionCount;
    private long mTotalBytes;

    internal bool TryReserveBytes(long byteCount)
    {
        if (byteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        bool result = byteCount <= mMaxTotalBytes - mTotalBytes;
        if (result)
            mTotalBytes += byteCount;
        return result;
    }

    internal bool TryReserveSections(int sectionCount)
    {
        if (sectionCount < 0)
            throw new ArgumentOutOfRangeException(nameof(sectionCount));
        bool result = sectionCount <= mMaxSectionCount - mSectionCount;
        if (result)
            mSectionCount += sectionCount;
        return result;
    }
}
