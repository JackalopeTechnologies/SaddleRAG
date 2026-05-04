// LibraryDetailPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.Components;
using SaddleRAG.Core.Models;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class LibraryDetailPageBase : ComponentBase
{
    [Parameter]
    public string LibraryId { get; set; } = string.Empty;

    [Inject]
    private MonitorDataService? DataService { get; set; }

    protected LibraryDetailData? Detail { get; private set; }
    protected LibraryProfile? Profile { get; private set; }
    protected string? LatestJobId { get; private set; }

    protected override async Task OnParametersSetAsync()
    {
        ArgumentNullException.ThrowIfNull(DataService);
        Detail = await DataService.GetLibraryDetailAsync(LibraryId);
        if (Detail is not null)
        {
            Profile = await DataService.GetLibraryProfileAsync(LibraryId, Detail.Version);
        }
    }
}
