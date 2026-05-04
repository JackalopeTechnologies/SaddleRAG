// JobHistoryPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.Components;
using SaddleRAG.Core.Enums;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Code-behind for the /monitor/jobs index page. Loads recent
///     <see cref="MonitorJobService.JobHistoryRow" /> rows with optional
///     status, library-substring, and limit filters.
/// </summary>
public abstract class JobHistoryPageBase : ComponentBase
{
    [Inject]
    private MonitorJobService? Jobs { get; set; }

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
    ///     Maximum number of rows to fetch.
    /// </summary>
    protected int LimitChoice { get; set; } = DefaultLimit;

    /// <summary>
    ///     The set of valid status names offered by the filter dropdown.
    /// </summary>
    protected static readonly string[] pmStatusChoices = Enum.GetNames<ScrapeJobStatus>();

    private const int DefaultLimit = 100;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
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

        Rows = await Jobs.ListAsync(statusEnum, LibraryFilter, LimitChoice);
    }
}
