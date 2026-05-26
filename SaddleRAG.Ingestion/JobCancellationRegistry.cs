// JobCancellationRegistry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Collections.Concurrent;
using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Ingestion;

/// <summary>
///     Default <see cref="IJobCancellationRegistry" /> implementation:
///     a <see cref="ConcurrentDictionary{TKey,TValue}" /> keyed by job
///     id. Singleton — every runner shares one instance so the
///     <c>cancel_job</c> MCP tool can find any in-flight cancellable job
///     without knowing which runner spawned it.
/// </summary>
public class JobCancellationRegistry : IJobCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> mEntries =
        new ConcurrentDictionary<string, CancellationTokenSource>();

    public void Register(string jobId, CancellationTokenSource cts)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(cts);

        mEntries[jobId] = cts;
    }

    public void Unregister(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        mEntries.TryRemove(jobId, out var _);
    }

    public async Task<bool> TryCancelAsync(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);

        bool res = false;
        if (mEntries.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync();
            res = true;
        }

        return res;
    }
}
