// IMonitorBroadcaster.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings
using SaddleRAG.Core.Models.Monitor;
#endregion

namespace SaddleRAG.Core.Interfaces;

/// <summary>
///     Records live pipeline events and pushes them to SignalR subscribers.
/// </summary>
public interface IMonitorBroadcaster
{
    void RecordJobStarted(string jobId, string libraryId, string version, string rootUrl);
    void RecordFetch(string jobId, string url);
    void RecordReject(string jobId, string url, string reason);
    void RecordError(string jobId, string message);
    void RecordPageClassified(string jobId);
    void RecordChunkGenerated(string jobId);
    void RecordChunkEmbedded(string jobId);
    void RecordPageCompleted(string jobId);
    void RecordJobCompleted(string jobId, int indexedPageCount);
    void RecordJobFailed(string jobId, string errorMessage);
    void RecordJobCancelled(string jobId);
    void RecordSuspectFlag(string jobId, string libraryId, string version, IReadOnlyList<string> reasons);

    JobTickSnapshot? GetJobSnapshot(string jobId);
    IReadOnlyList<string> GetActiveJobIds();

    void Subscribe(string jobId, Func<JobTickEvent, Task> handler);
    void Unsubscribe(string jobId, Func<JobTickEvent, Task> handler);

    void BroadcastTick(string jobId);
}
