// LandingPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using MudBlazor;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class LandingPageBase : ComponentBase, IAsyncDisposable
{
    [Inject]
    private NavigationManager? Nav { get; set; }

    [Inject]
    private MonitorWriteService? WriteService { get; set; }

    [Inject]
    private MonitorDataService? DataService { get; set; }

    [Inject]
    protected IMonitorBroadcaster? Broadcaster { get; set; }

    [Inject]
    private ISnackbar? Snackbar { get; set; }

    protected List<JobTickSnapshot> ActiveJobSnapshots { get; } = [];
    protected List<LibrarySummaryItem> Libraries { get; } = [];

    private HubConnection? mHub;
    private Timer? mRecoveryTimer;
    private readonly CancellationTokenSource mShutdown = new();
    private bool mRefreshing;
    protected bool LibrariesUnavailable { get; private set; }

    public async ValueTask DisposeAsync()
    {
        mRecoveryTimer?.Dispose();
        await mShutdown.CancelAsync();
        if (mHub is not null)
            await mHub.DisposeAsync();
    }

    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(Nav);
        ArgumentNullException.ThrowIfNull(DataService);
        ArgumentNullException.ThrowIfNull(Broadcaster);

        await RefreshLibrariesAsync();
        mRecoveryTimer = new Timer(_ => _ = InvokeAsync(RetryLibrariesAsync), null,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        RebuildFromIds(Broadcaster.GetActiveJobIds());

        mHub = new HubConnectionBuilder()
               .WithUrl(Nav.ToAbsoluteUri(HubPath))
               .WithAutomaticReconnect()
               .Build();

        mHub.On<IReadOnlyList<string>>(ActiveJobsEvent,
                                       async ids =>
                                       {
                                           RebuildFromIds(ids);
                                           await InvokeAsync(StateHasChanged);
                                       }
                                      );

        await mHub.StartAsync();
        await mHub.InvokeAsync(SubscribeLandingMethod);
    }

    protected void RebuildFromIds(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(Broadcaster);
        ActiveJobSnapshots.Clear();
        var snapshots = ids.Select(id => Broadcaster.GetJobSnapshot(id))
                           .OfType<JobTickSnapshot>();
        ActiveJobSnapshots.AddRange(snapshots);
    }

    private async Task RetryLibrariesAsync()
    {
        if (LibrariesUnavailable && !mRefreshing)
        {
            await RefreshLibrariesAsync();
            StateHasChanged();
        }
    }

    private async Task RefreshLibrariesAsync()
    {
        ArgumentNullException.ThrowIfNull(DataService);
        mRefreshing = true;
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(mShutdown.Token);
            bounded.CancelAfter(TimeSpan.FromSeconds(3));
            var summaries = await DataService.GetLibrarySummariesAsync(bounded.Token);
            Libraries.Clear();
            Libraries.AddRange(summaries);
            LibrariesUnavailable = false;
        }
        catch (Exception) when (!mShutdown.IsCancellationRequested)
        {
            // Keep the last successful view and an explicit failure banner. The
            // health page supplies the dependency diagnosis independently.
            LibrariesUnavailable = true;
        }
        finally
        {
            mRefreshing = false;
        }
    }

    protected async Task CancelJob(string jobId)
    {
        ArgumentNullException.ThrowIfNull(WriteService);
        ArgumentNullException.ThrowIfNull(Snackbar);
        bool accepted = await WriteService.CancelJobAsync(jobId);
        if (!accepted)
            Snackbar.Add($"Cancel request for job {jobId} was rejected by the server.", Severity.Error);
    }

    private const string HubPath = "/monitor/hub";
    private const string ActiveJobsEvent = "ActiveJobs";
    private const string SubscribeLandingMethod = "SubscribeLanding";
}
