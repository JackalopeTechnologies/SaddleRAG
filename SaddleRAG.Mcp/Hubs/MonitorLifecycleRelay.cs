// MonitorLifecycleRelay.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.SignalR;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Mcp.Hubs;

/// <summary>
///     Subscribes to <see cref="IMonitorEvents" /> and forwards each lifecycle
///     event to all connected SignalR clients via <see cref="IHubContext{MonitorHub}" />.
/// </summary>
public sealed class MonitorLifecycleRelay : IHostedService
{
    /// <summary>
    ///     Initializes a new instance of <see cref="MonitorLifecycleRelay" />.
    /// </summary>
    public MonitorLifecycleRelay(IMonitorEvents events, IHubContext<MonitorHub> hub)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(hub);
        mEvents = events;
        mHub = hub;
    }

    private readonly IMonitorEvents mEvents;
    private readonly IHubContext<MonitorHub> mHub;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct)
    {
        mEvents.JobStarted        += OnJobStarted;
        mEvents.JobCompleted      += OnJobCompleted;
        mEvents.JobFailed         += OnJobFailed;
        mEvents.JobCancelled      += OnJobCancelled;
        mEvents.SuspectFlagRaised += OnSuspectFlag;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct)
    {
        mEvents.JobStarted        -= OnJobStarted;
        mEvents.JobCompleted      -= OnJobCompleted;
        mEvents.JobFailed         -= OnJobFailed;
        mEvents.JobCancelled      -= OnJobCancelled;
        mEvents.SuspectFlagRaised -= OnSuspectFlag;
        return Task.CompletedTask;
    }

    private void OnJobStarted(JobStartedEvent e) => Send(JobStartedMethod, e);
    private void OnJobCompleted(JobCompletedEvent e) => Send(JobCompletedMethod, e);
    private void OnJobFailed(JobFailedEvent e) => Send(JobFailedMethod, e);
    private void OnJobCancelled(JobCancelledEvent e) => Send(JobCancelledMethod, e);
    private void OnSuspectFlag(SuspectFlagEvent e) => Send(SuspectFlagMethod, e);

    private void Send<T>(string method, T payload)
    {
        _ = mHub.Clients.All.SendAsync(method, payload);
    }

    private const string JobStartedMethod = "JobStarted";
    private const string JobCompletedMethod = "JobCompleted";
    private const string JobFailedMethod = "JobFailed";
    private const string JobCancelledMethod = "JobCancelled";
    private const string SuspectFlagMethod = "SuspectFlag";
}
