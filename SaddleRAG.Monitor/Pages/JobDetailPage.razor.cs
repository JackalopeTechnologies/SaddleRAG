// JobDetailPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class JobDetailPageBase : ComponentBase, IAsyncDisposable
{
    [Parameter]
    public string JobId { get; set; } = string.Empty;

    [Inject]
    private NavigationManager? Nav { get; set; }

    [Inject]
    private MonitorWriteService? WriteService { get; set; }

    protected JobTickEvent? CurrentTick { get; private set; }
    protected bool HubConnected { get; private set; } = true;
    private CancellationTokenSource mFallbackCts = new CancellationTokenSource();

    private HubConnection? mHub;

    public async ValueTask DisposeAsync()
    {
        mFallbackCts.Cancel();
        mFallbackCts.Dispose();
        if (mHub is not null)
            await mHub.DisposeAsync();
    }

    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(Nav);
        mHub = new HubConnectionBuilder()
               .WithUrl(Nav.ToAbsoluteUri(HubPath))
               .WithAutomaticReconnect()
               .Build();

        mHub.On<JobTickEvent>(JobTickMethod,
                              async tick =>
                              {
                                  CurrentTick = tick;
                                  await InvokeAsync(StateHasChanged);
                              }
                             );

        mHub.Closed += async _ =>
                       {
                           HubConnected = false;
                           StartFallbackPolling();
                           await InvokeAsync(StateHasChanged);
                       };

        mHub.Reconnected += async _ =>
                            {
                                HubConnected = true;
                                mFallbackCts.Cancel();
                                await InvokeAsync(StateHasChanged);
                            };

        await mHub.StartAsync();
        await mHub.InvokeAsync(SubscribeJobMethod, JobId);
    }

    private void StartFallbackPolling()
    {
        mFallbackCts.Cancel();
        mFallbackCts.Dispose();
        mFallbackCts = new CancellationTokenSource();
        _ = RunFallbackLoopAsync(mFallbackCts.Token);
    }

    private async Task RunFallbackLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(FallbackPollSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await PollOnceAsync(ct);
        }
        catch(OperationCanceledException)
        {
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (WriteService is not null)
        {
            var snap = await WriteService.GetJobSnapshotAsync(JobId, ct);
            if (snap is not null)
            {
                CurrentTick = SnapshotToTick(snap);
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private static JobTickEvent SnapshotToTick(JobTickSnapshot snap) =>
        new JobTickEvent
            {
                JobId = snap.JobId,
                At = DateTime.UtcNow,
                Counters = snap.Counters,
                CurrentHost = snap.CurrentHost,
                RecentFetches = snap.RecentFetches,
                RecentRejects = snap.RecentRejects,
                ErrorsThisTick = snap.RecentErrors
            };

    private const string HubPath = "/monitor/hub";
    private const string JobTickMethod = "JobTick";
    private const string SubscribeJobMethod = "SubscribeJob";
    private const int FallbackPollSeconds = 3;
}
