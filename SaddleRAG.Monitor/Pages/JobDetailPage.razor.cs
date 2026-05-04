// JobDetailPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using MudBlazor;
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

    [Inject]
    private MonitorDataService? DataService { get; set; }

    protected JobTickEvent? CurrentTick { get; private set; }
    protected bool HubConnected { get; private set; } = true;
    protected JobInfo? Info { get; private set; }
    protected PipelineRates Rates { get; private set; } = PipelineRates.Zero;
    protected List<RecentError> AllErrors { get; } = [];
    protected IReadOnlyList<RecentFetch> TerminalFetches { get; private set; } = Array.Empty<RecentFetch>();
    protected IReadOnlyList<RecentReject> TerminalRejects { get; private set; } = Array.Empty<RecentReject>();

    protected bool IsActive => Info is not null
                            && (Info.Status is "Queued" or "Running")
                            && Info.CompletedAt is null;

    protected string Elapsed => Info?.StartedAt is null
                                    ? "—"
                                    : (Info.CompletedAt ?? DateTime.UtcNow).Subtract(Info.StartedAt.Value)
                                                                           .ToString(@"hh\:mm\:ss");

    protected Severity TerminalSeverity => Info?.Status switch
    {
        "Completed" => Severity.Success,
        "Failed"    => Severity.Error,
        "Cancelled" => Severity.Warning,
        var _       => Severity.Info
    };

    protected static Color StatusColor(string status) => status switch
    {
        "Running"   => Color.Info,
        "Queued"    => Color.Default,
        "Completed" => Color.Success,
        "Failed"    => Color.Error,
        "Cancelled" => Color.Warning,
        var _       => Color.Default
    };

    private CancellationTokenSource mFallbackCts = new CancellationTokenSource();
    private HubConnection? mHub;
    private System.Threading.Timer? mElapsedTimer;
    private readonly RatesAccumulator mRates = new RatesAccumulator();

    public async ValueTask DisposeAsync()
    {
        mFallbackCts.Cancel();
        mFallbackCts.Dispose();
        if (mElapsedTimer is not null)
            await mElapsedTimer.DisposeAsync();
        if (mHub is not null)
            await mHub.DisposeAsync();
    }

    protected async Task CancelClicked()
    {
        ArgumentNullException.ThrowIfNull(WriteService);
        await WriteService.CancelJobAsync(JobId);
    }

    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(Nav);
        ArgumentNullException.ThrowIfNull(DataService);

        Info = await DataService.GetJobInfoAsync(JobId);

        mHub = new HubConnectionBuilder()
               .WithUrl(Nav.ToAbsoluteUri(HubPath))
               .WithAutomaticReconnect()
               .Build();

        mHub.On<JobTickEvent>(JobTickMethod,
                              async tick =>
                              {
                                  CurrentTick = tick;
                                  Rates = mRates.Update(tick.Counters, tick.At);
                                  IngestTick(tick);
                                  await InvokeAsync(StateHasChanged);
                              }
                             );

        mHub.On<JobCompletedEvent>(JobCompletedMethod,
                                   async e =>
                                   {
                                       if (e.JobId == JobId)
                                           await OnLifecycleTransitionAsync();
                                   }
                                  );
        mHub.On<JobFailedEvent>(JobFailedMethod,
                                async e =>
                                {
                                    if (e.JobId == JobId)
                                        await OnLifecycleTransitionAsync();
                                }
                               );
        mHub.On<JobCancelledEvent>(JobCancelledMethod,
                                   async e =>
                                   {
                                       if (e.JobId == JobId)
                                           await OnLifecycleTransitionAsync();
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

        if (!IsActive)
            await LoadTerminalFeedsAsync();

        if (IsActive)
        {
            mElapsedTimer = new System.Threading.Timer(_ => InvokeAsync(StateHasChanged),
                                                       state: null,
                                                       dueTime: TimeSpan.FromSeconds(1),
                                                       period: TimeSpan.FromSeconds(1));
        }
    }

    private async Task LoadTerminalFeedsAsync()
    {
        ArgumentNullException.ThrowIfNull(DataService);
        var (fetches, rejects) = await DataService.GetTerminalFeedsAsync(JobId);
        TerminalFetches = fetches;
        TerminalRejects = rejects;
    }

    private async Task OnLifecycleTransitionAsync()
    {
        ArgumentNullException.ThrowIfNull(DataService);
        Info = await DataService.GetJobInfoAsync(JobId);
        await LoadTerminalFeedsAsync();
        if (!IsActive && mElapsedTimer is not null)
        {
            await mElapsedTimer.DisposeAsync();
            mElapsedTimer = null;
        }

        await InvokeAsync(StateHasChanged);
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
                var tick = SnapshotToTick(snap);
                CurrentTick = tick;
                Rates = mRates.Update(tick.Counters, tick.At);
                IngestTick(tick);
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private void IngestTick(JobTickEvent tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        foreach (var err in tick.ErrorsThisTick)
            AllErrors.Add(err);
        while (AllErrors.Count > MaxErrorsRetained)
            AllErrors.RemoveAt(0);
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
    private const string JobCompletedMethod = "JobCompleted";
    private const string JobFailedMethod = "JobFailed";
    private const string JobCancelledMethod = "JobCancelled";
    private const string SubscribeJobMethod = "SubscribeJob";
    private const int FallbackPollSeconds = 3;
    private const int MaxErrorsRetained = 200;
}
