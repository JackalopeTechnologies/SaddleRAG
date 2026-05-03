// MonitorTickService.cs
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
///     Background service that pushes 750 ms tick events to SignalR groups
///     for each active job, and landing-page heartbeats.
/// </summary>
public sealed class MonitorTickService : BackgroundService
{
    /// <summary>
    ///     Initializes a new instance of <see cref="MonitorTickService"/>.
    /// </summary>
    public MonitorTickService(IMonitorBroadcaster broadcaster,
                              IHubContext<MonitorHub> hub)
    {
        mBroadcaster = broadcaster;
        mHub         = hub;
    }

    private readonly IMonitorBroadcaster     mBroadcaster;
    private readonly IHubContext<MonitorHub> mHub;

    private const int TickIntervalMs = 750;

    private static readonly TimeSpan smTickInterval = TimeSpan.FromMilliseconds(TickIntervalMs);

    private const string JobTickMethod    = "JobTick";
    private const string ActiveJobsMethod = "ActiveJobs";

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(smTickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PushTicksAsync(stoppingToken);
    }

    private async Task PushTicksAsync(CancellationToken ct)
    {
        foreach (var jobId in mBroadcaster.GetActiveJobIds())
        {
            var snapshot = mBroadcaster.GetJobSnapshot(jobId);
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
                await mHub.Clients.Group(MonitorHub.GroupName(jobId))
                          .SendAsync(JobTickMethod, tick, cancellationToken: ct);
            }
        }

        var activeIds = mBroadcaster.GetActiveJobIds();
        await mHub.Clients.Group(MonitorHub.LandingGroup)
                  .SendAsync(ActiveJobsMethod, activeIds, cancellationToken: ct);
    }
}
