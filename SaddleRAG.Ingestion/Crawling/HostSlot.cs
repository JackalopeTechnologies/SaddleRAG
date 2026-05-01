// HostSlot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>
///     RAII slot returned by <see cref="HostRateLimiter.AcquireAsync"/>.
///     Disposing returns the permit to the pool.
/// </summary>
public readonly struct HostSlot : IDisposable
{
    public HostSlot(HostRateLimiter limiter)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        mLimiter = limiter;
    }

    private readonly HostRateLimiter mLimiter;

    public void Dispose()
    {
        mLimiter.Release();
    }
}
