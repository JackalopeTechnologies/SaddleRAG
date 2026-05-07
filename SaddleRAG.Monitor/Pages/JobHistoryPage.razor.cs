// JobHistoryPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Code-behind for the /monitor/jobs index page. Loads recent
///     <see cref="MonitorJobService.JobHistoryRow" /> rows with optional
///     status, library-substring, and limit filters. Subscribes to
///     <c>/monitor/hub</c> for live updates: <c>JobProgress</c> patches
///     running rows in-place; lifecycle transitions re-fetch the full list.
/// </summary>
public abstract class JobHistoryPageBase : ComponentBase, IAsyncDisposable
{
    [Inject]
    private MonitorJobService? Jobs { get; set; }

    [Inject]
    private NavigationManager? Nav { get; set; }

    /// <summary>
    ///     Rows currently displayed by the page.
    /// </summary>
    protected IReadOnlyList<MonitorJobService.JobHistoryRow> Rows { get; private set; } = [];

    /// <summary>
    ///     Selected status filter (string name of <see cref="ScrapeJobStatus" />)
    ///     or null/empty for "all statuses".
    /// </summary>
    protected string? StatusFilter { get; set; }

    /// <summary>
    ///     Case-insensitive substring filter for the library id; null/empty = no filter.
    /// </summary>
    protected string? LibraryFilter { get; set; }

    /// <summary>
    ///     Selected job type filter or null for "all types".
    /// </summary>
    protected JobType? TypeFilter { get; set; }

    protected static readonly JobType[] pmTypeChoices = Enum.GetValues<JobType>();

    /// <summary>
    ///     Maximum number of rows to fetch.
    /// </summary>
    protected int LimitChoice { get; set; } = DefaultLimit;

    /// <summary>
    ///     The set of valid status names offered by the filter dropdown.
    /// </summary>
    protected static readonly string[] pmStatusChoices = Enum.GetNames<ScrapeJobStatus>();

    private HubConnection? mHub;
    private bool mDisposed;

    private const int DefaultLimit = 100;
    private const string HubPath = "/monitor/hub";
    private const string JobStartedMethod = "JobStarted";
    private const string JobProgressMethod = "JobProgress";
    private const string JobCompletedMethod = "JobCompleted";
    private const string JobFailedMethod = "JobFailed";
    private const string JobCancelledMethod = "JobCancelled";

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        mDisposed = true;
        if (mHub is not null)
            await mHub.DisposeAsync();
    }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(Nav);

        await LoadAsync();

        mHub = new HubConnectionBuilder()
               .WithUrl(Nav.ToAbsoluteUri(HubPath))
               .WithAutomaticReconnect()
               .Build();

        mHub.On<JobStartedEvent>(JobStartedMethod,
                                 async _ =>
                                 {
                                     if (!mDisposed)
                                     {
                                         await LoadAsync();
                                         await InvokeAsync(StateHasChanged);
                                     }
                                 });

        mHub.On<JobProgressEvent>(JobProgressMethod,
                                  async e =>
                                  {
                                      if (!mDisposed)
                                      {
                                          Rows = Rows.Select(r => r.JobId == e.JobId
                                                                      ? r with
                                                                        {
                                                                            ItemsProcessed = e.ItemsProcessed,
                                                                            ItemsTotal     = e.ItemsTotal,
                                                                            ItemsLabel     = e.ItemsLabel
                                                                        }
                                                                      : r)
                                                     .ToList();
                                          await InvokeAsync(StateHasChanged);
                                      }
                                  });

        mHub.On<JobCompletedEvent>(JobCompletedMethod,
                                   async _ =>
                                   {
                                       if (!mDisposed)
                                       {
                                           await LoadAsync();
                                           await InvokeAsync(StateHasChanged);
                                       }
                                   });

        mHub.On<JobFailedEvent>(JobFailedMethod,
                                async _ =>
                                {
                                    if (!mDisposed)
                                    {
                                        await LoadAsync();
                                        await InvokeAsync(StateHasChanged);
                                    }
                                });

        mHub.On<JobCancelledEvent>(JobCancelledMethod,
                                   async _ =>
                                   {
                                       if (!mDisposed)
                                       {
                                           await LoadAsync();
                                           await InvokeAsync(StateHasChanged);
                                       }
                                   });

        await mHub.StartAsync();
    }

    /// <summary>
    ///     Reloads <see cref="Rows" /> using the current filter values.
    /// </summary>
    protected async Task LoadAsync()
    {
        ArgumentNullException.ThrowIfNull(Jobs);
        ScrapeJobStatus? statusEnum = null;
        if (!string.IsNullOrEmpty(StatusFilter)
         && Enum.TryParse<ScrapeJobStatus>(StatusFilter, ignoreCase: true, out var parsed))
        {
            statusEnum = parsed;
        }

        Rows = await Jobs.ListAsync(statusEnum, TypeFilter, LibraryFilter, LimitChoice);
    }
}
