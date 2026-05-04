// LandingPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
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

    protected List<JobTickSnapshot> ActiveJobSnapshots { get; } = [];
    protected List<LibrarySummaryItem> Libraries { get; } = [];

    private HubConnection? mHub;

    public async ValueTask DisposeAsync()
    {
        if (mHub is not null)
            await mHub.DisposeAsync();
    }

    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(Nav);
        ArgumentNullException.ThrowIfNull(DataService);
        ArgumentNullException.ThrowIfNull(Broadcaster);

        var summaries = await DataService.GetLibrarySummariesAsync();
        Libraries.Clear();
        Libraries.AddRange(summaries);

        RebuildFromIds(Broadcaster.GetActiveJobIds());

        mHub = new HubConnectionBuilder()
               .WithUrl(Nav.ToAbsoluteUri(HubPath))
               .WithAutomaticReconnect()
               .Build();

        mHub.On<IReadOnlyList<string>>(ActiveJobsEvent, async ids =>
        {
            RebuildFromIds(ids);
            await InvokeAsync(StateHasChanged);
        });

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

    protected async Task CancelJob(string jobId)
    {
        ArgumentNullException.ThrowIfNull(WriteService);
        await WriteService.CancelJobAsync(jobId);
    }

    private const string HubPath = "/monitor/hub";
    private const string ActiveJobsEvent = "ActiveJobs";
    private const string SubscribeLandingMethod = "SubscribeLanding";
}
