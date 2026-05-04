// MonitorHub.cs
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
///     SignalR hub that manages subscriptions for the scrape-diagnostics monitor.
/// </summary>
public sealed class MonitorHub : Hub
{
    /// <summary>
    ///     Initializes a new instance of <see cref="MonitorHub" />.
    /// </summary>
    public MonitorHub(IMonitorBroadcaster broadcaster)
    {
        mBroadcaster = broadcaster;
    }

    private readonly IMonitorBroadcaster mBroadcaster;

    /// <summary>
    ///     Returns the SignalR group name for a specific job.
    /// </summary>
    public static string GroupName(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        return $"job-{jobId}";
    }

    /// <summary>
    ///     Subscribe to tick events for a specific job. Called by the job-detail page.
    ///     Sends the current snapshot immediately so the page doesn't wait for the first tick.
    /// </summary>
    public async Task SubscribeJob(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(jobId));
        var snapshot = mBroadcaster.GetJobSnapshot(jobId);
        var sendTask = snapshot is not null
                           ? Clients.Caller.SendAsync(JobTickMethod, BuildTick(jobId, snapshot))
                           : Task.CompletedTask;
        await sendTask;
    }

    /// <summary>
    ///     Subscribe to landing-page coarse updates (active job list + aggregate counters).
    /// </summary>
    public async Task SubscribeLanding()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, LandingGroup);
    }

    internal static JobTickEvent BuildTick(string jobId, JobTickSnapshot snap) =>
        new JobTickEvent
            {
                JobId = jobId,
                At = DateTime.UtcNow,
                Counters = snap.Counters,
                CurrentHost = snap.CurrentHost,
                RecentFetches = snap.RecentFetches,
                RecentRejects = snap.RecentRejects,
                ErrorsThisTick = snap.RecentErrors
            };

    private const string JobTickMethod = "JobTick";

    /// <summary>
    ///     Group name for the landing-page coarse-update feed.
    /// </summary>
    public const string LandingGroup = "landing";
}
