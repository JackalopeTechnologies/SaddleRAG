// MonitorBroadcaster.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings
using System.Collections.Concurrent;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
#endregion

namespace SaddleRAG.Ingestion.Diagnostics;

public sealed class MonitorBroadcaster : IMonitorBroadcaster
{
    private sealed class JobState
    {
        public required string  JobId     { get; init; }
        public required string  LibraryId { get; init; }
        public required string  Version   { get; init; }
        public required string  RootUrl   { get; init; }

        public int     PagesQueued     { get; set; }
        public int     PagesFetched    { get; set; }
        public int     PagesClassified { get; set; }
        public int     ChunksGenerated { get; set; }
        public int     ChunksEmbedded  { get; set; }
        public int     PagesCompleted  { get; set; }
        public int     ErrorCount      { get; set; }
        public string? CurrentHost     { get; set; }

        public readonly Queue<RecentFetch>  pmRecentFetches = new();
        public readonly Queue<RecentReject> pmRecentRejects = new();
        public readonly Queue<RecentError>  pmRecentErrors  = new();
        public readonly object              pmLock          = new();
    }

    private const int RecentFeedCapacity = 50;
    private const int ErrorFeedCapacity  = 20;

    private readonly ConcurrentDictionary<string, JobState>                       mJobs        = new();
    private readonly ConcurrentDictionary<string, List<Func<JobTickEvent, Task>>> mSubscribers = new();

    public void RecordJobStarted(string jobId, string libraryId, string version, string rootUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentException.ThrowIfNullOrEmpty(rootUrl);
        var state = new JobState
        {
            JobId     = jobId,
            LibraryId = libraryId,
            Version   = version,
            RootUrl   = rootUrl
        };
        mJobs[jobId] = state;
    }

    public void RecordFetch(string jobId, string url)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (mJobs.TryGetValue(jobId, out var state))
        {
            lock (state.pmLock)
            {
                state.PagesFetched++;
                state.PagesQueued++;
                state.CurrentHost = SafeGetHost(url);
                EnqueueCapped(state.pmRecentFetches,
                              new RecentFetch { Url = url, At = DateTime.UtcNow },
                              RecentFeedCapacity);
            }
        }
    }

    public void RecordReject(string jobId, string url, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (mJobs.TryGetValue(jobId, out var state))
        {
            lock (state.pmLock)
                EnqueueCapped(state.pmRecentRejects,
                              new RecentReject { Url = url, Reason = reason, At = DateTime.UtcNow },
                              RecentFeedCapacity);
        }
    }

    public void RecordError(string jobId, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(message);
        if (mJobs.TryGetValue(jobId, out var state))
        {
            lock (state.pmLock)
            {
                state.ErrorCount++;
                EnqueueCapped(state.pmRecentErrors,
                              new RecentError { Message = message, At = DateTime.UtcNow },
                              ErrorFeedCapacity);
            }
        }
    }

    public void RecordPageClassified(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        Increment(jobId, s => s.PagesClassified++);
    }

    public void RecordChunkGenerated(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        Increment(jobId, s => s.ChunksGenerated++);
    }

    public void RecordChunkEmbedded(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        Increment(jobId, s => s.ChunksEmbedded++);
    }

    public void RecordPageCompleted(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        Increment(jobId, s => s.PagesCompleted++);
    }

    public void RecordJobCompleted(string jobId, int indexedPageCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        mJobs.TryRemove(jobId, out _);
    }

    public void RecordJobFailed(string jobId, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(errorMessage);
        mJobs.TryRemove(jobId, out _);
    }

    public void RecordJobCancelled(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        mJobs.TryRemove(jobId, out _);
    }

    public void RecordSuspectFlag(string jobId, string libraryId, string version,
                                  IReadOnlyList<string> reasons)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(libraryId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        ArgumentNullException.ThrowIfNull(reasons);
    }

    public JobTickSnapshot? GetJobSnapshot(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        JobTickSnapshot? result = null;
        if (mJobs.TryGetValue(jobId, out var state))
        {
            lock (state.pmLock)
            {
                result = new JobTickSnapshot
                {
                    JobId         = jobId,
                    CurrentHost   = state.CurrentHost,
                    Counters      = BuildCounters(state),
                    RecentFetches = state.pmRecentFetches.ToList(),
                    RecentRejects = state.pmRecentRejects.ToList(),
                    RecentErrors  = state.pmRecentErrors.ToList()
                };
            }
        }
        return result;
    }

    public IReadOnlyList<string> GetActiveJobIds() => mJobs.Keys.ToList();

    public void Subscribe(string jobId, Func<JobTickEvent, Task> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(handler);
        var list = mSubscribers.GetOrAdd(jobId, _ => []);
        lock (list)
            list.Add(handler);
    }

    public void Unsubscribe(string jobId, Func<JobTickEvent, Task> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentNullException.ThrowIfNull(handler);
        if (mSubscribers.TryGetValue(jobId, out var list))
        {
            lock (list)
                list.Remove(handler);
        }
    }

    public void BroadcastTick(string jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        var snapshot = GetJobSnapshot(jobId);
        if (snapshot is not null)
        {
            var tick = new JobTickEvent
            {
                JobId          = jobId,
                At             = DateTime.UtcNow,
                Counters       = snapshot.Counters,
                CurrentHost    = snapshot.CurrentHost,
                RecentFetches  = snapshot.RecentFetches,
                RecentRejects  = snapshot.RecentRejects,
                ErrorsThisTick = snapshot.RecentErrors
            };

            if (mSubscribers.TryGetValue(jobId, out var handlers))
            {
                List<Func<JobTickEvent, Task>> handlerSnapshot;
                lock (handlers)
                    handlerSnapshot = [..handlers];

                foreach (var handler in handlerSnapshot)
                    _ = handler(tick);
            }
        }
    }

    private void Increment(string jobId, Action<JobState> mutate)
    {
        if (mJobs.TryGetValue(jobId, out var state))
        {
            lock (state.pmLock)
                mutate(state);
        }
    }

    private static void EnqueueCapped<T>(Queue<T> queue, T item, int cap)
    {
        queue.Enqueue(item);
        while (queue.Count > cap)
            queue.Dequeue();
    }

    private static PipelineCounters BuildCounters(JobState s) => new()
    {
        PagesQueued     = s.PagesQueued,
        PagesFetched    = s.PagesFetched,
        PagesClassified = s.PagesClassified,
        ChunksGenerated = s.ChunksGenerated,
        ChunksEmbedded  = s.ChunksEmbedded,
        PagesCompleted  = s.PagesCompleted,
        ErrorCount      = s.ErrorCount
    };

    private static string SafeGetHost(string url)
    {
        string result = string.Empty;
        try
        {
            result = new Uri(url).Host;
        }
        catch (UriFormatException)
        {
        }
        return result;
    }
}
