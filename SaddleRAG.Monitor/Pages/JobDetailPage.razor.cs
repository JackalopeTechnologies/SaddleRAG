// JobDetailPage.razor.cs
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

public abstract class JobDetailPageBase : ComponentBase, IAsyncDisposable
{
    [Parameter] public string JobId { get; set; } = string.Empty;
    [Inject] private NavigationManager? Nav { get; set; }

    protected JobTickEvent? CurrentTick { get; private set; }

    private HubConnection? mHub;

    private const string HubPath            = "/monitor/hub";
    private const string JobTickMethod      = "JobTick";
    private const string SubscribeJobMethod = "SubscribeJob";

    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(Nav);
        mHub = new HubConnectionBuilder()
            .WithUrl(Nav.ToAbsoluteUri(HubPath))
            .WithAutomaticReconnect()
            .Build();

        mHub.On<JobTickEvent>(JobTickMethod, async tick =>
        {
            CurrentTick = tick;
            await InvokeAsync(StateHasChanged);
        });

        mHub.Closed += async _ =>
            await InvokeAsync(StateHasChanged);

        await mHub.StartAsync();
        await mHub.InvokeAsync(SubscribeJobMethod, JobId);
    }

    public async ValueTask DisposeAsync()
    {
        if (mHub is not null)
            await mHub.DisposeAsync();
    }
}
