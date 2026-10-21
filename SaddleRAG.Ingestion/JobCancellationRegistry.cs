// JobCancellationRegistry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

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
    private readonly ConcurrentDictionary<string, RunningJobRegistration> mEntries = new();

    public void Register(string jobId, CancellationTokenSource cts)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(cts);

        Register(jobId, cts, null);
    }

    /// <inheritdoc />
    public void Register(string jobId, CancellationTokenSource cts, string? profile)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(cts);
        mEntries[jobId] = new RunningJobRegistration(cts, profile);
    }

    /// <summary>Snapshot used to stop only jobs whose database is unavailable.</summary>
    public IReadOnlyDictionary<string, string?> GetProfiles() =>
        mEntries.ToDictionary(entry => entry.Key, entry => entry.Value.Profile);

    /// <summary>Whether a runner still owns the job.</summary>
    public bool IsRegistered(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        return mEntries.ContainsKey(jobId);
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
        if (mEntries.TryGetValue(jobId, out var registration))
        {
            try
            {
                await registration.Cancellation.CancelAsync();
                res = true;
            }
            catch (ObjectDisposedException)
            {
                // The runner finished and disposed its token after this lookup.
            }
        }

        return res;
    }
}
