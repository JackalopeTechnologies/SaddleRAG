// MonitorTickService.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.SignalR;
using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Mcp.Hubs;

/// <summary>
///     Background service that pushes 750 ms tick events to SignalR groups
///     for each active job, and landing-page heartbeats.
/// </summary>
public sealed class MonitorTickService : BackgroundService
{
    /// <summary>
    ///     Initializes a new instance of <see cref="MonitorTickService" />.
    /// </summary>
    public MonitorTickService(IMonitorBroadcaster broadcaster,
                              IHubContext<MonitorHub> hub)
    {
        mBroadcaster = broadcaster;
        mHub = hub;
    }

    private readonly IMonitorBroadcaster mBroadcaster;
    private readonly IHubContext<MonitorHub> mHub;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(smTickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PushTicksAsync(stoppingToken);
    }

    private async Task PushTicksAsync(CancellationToken ct)
    {
        var activeIds = mBroadcaster.GetActiveJobIds();
        foreach(var jobId in activeIds)
        {
            var snapshot = mBroadcaster.GetJobSnapshot(jobId);
            var sendTask = snapshot is not null
                               ? mHub.Clients.Group(MonitorHub.GroupName(jobId))
                                     .SendAsync(JobTickMethod,
                                                MonitorHub.BuildTick(jobId, snapshot),
                                                ct
                                               )
                               : Task.CompletedTask;
            await sendTask;
        }

        await mHub.Clients.Group(MonitorHub.LandingGroup)
                  .SendAsync(ActiveJobsMethod, activeIds, ct);
    }

    private const int TickIntervalMs = 750;

    private const string JobTickMethod = "JobTick";
    private const string ActiveJobsMethod = "ActiveJobs";

    private static readonly TimeSpan smTickInterval = TimeSpan.FromMilliseconds(TickIntervalMs);
}
