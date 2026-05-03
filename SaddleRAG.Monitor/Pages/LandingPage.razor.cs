// LandingPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using SaddleRAG.Core.Models.Monitor;
#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class LandingPageBase : ComponentBase, IAsyncDisposable
{
    [Inject] private NavigationManager? Nav { get; set; }

    protected List<JobTickSnapshot>    ActiveJobSnapshots { get; } = [];
    protected List<LibrarySummaryItem> Libraries          { get; } = [];

    private HubConnection? mHub;

    private const string HubPath              = "/monitor/hub";
    private const string ActiveJobsEvent      = "ActiveJobs";
    private const string SubscribeLandingMethod = "SubscribeLanding";

    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(Nav);
        mHub = new HubConnectionBuilder()
            .WithUrl(Nav.ToAbsoluteUri(HubPath))
            .WithAutomaticReconnect()
            .Build();

        mHub.On<IReadOnlyList<string>>(ActiveJobsEvent, async _ =>
        {
            await InvokeAsync(StateHasChanged);
        });

        await mHub.StartAsync();
        await mHub.InvokeAsync(SubscribeLandingMethod);
    }

    protected Task CancelJob(string jobId)
    {
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (mHub is not null)
            await mHub.DisposeAsync();
    }
}
