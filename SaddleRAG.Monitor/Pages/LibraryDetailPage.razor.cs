// LibraryDetailPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.AspNetCore.Components;
using MudBlazor;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Monitor.Components;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

public abstract class LibraryDetailPageBase : ComponentBase
{
    [Parameter]
    public string LibraryId { get; set; } = string.Empty;

    [Inject]
    private MonitorDataService? DataService { get; set; }

    [Inject]
    private MonitorWriteService? WriteService { get; set; }

    [Inject]
    private IDialogService? DialogService { get; set; }

    [Inject]
    private NavigationManager? Nav { get; set; }

    [Inject]
    private ISnackbar? Snackbar { get; set; }

    protected LibraryDetailData? Detail { get; private set; }
    protected LibraryProfile? Profile { get; private set; }
    protected string? LatestJobId { get; private set; }
    protected AuditSummary? AuditSummary { get; private set; }
    protected IReadOnlyList<LibraryVersionRecord> Versions { get; private set; } = [];

    protected override async Task OnParametersSetAsync()
    {
        ArgumentNullException.ThrowIfNull(DataService);
        Detail = await DataService.GetLibraryDetailAsync(LibraryId);
        if (Detail is not null)
        {
            Profile = await DataService.GetLibraryProfileAsync(LibraryId, Detail.Version);
            Versions = await DataService.GetVersionsAsync(LibraryId);
            LatestJobId = await DataService.GetLatestJobIdAsync(LibraryId, Detail.Version);
            if (!string.IsNullOrEmpty(LatestJobId))
                AuditSummary = await DataService.GetAuditSummaryAsync(LatestJobId);
        }
    }

    protected async Task RescrapeAsync()
    {
        ArgumentNullException.ThrowIfNull(WriteService);
        ArgumentNullException.ThrowIfNull(Snackbar);
        if (Detail is not null)
        {
            var jobId = await WriteService.RescrapeAsync(Detail.LibraryId, Detail.Version);
            if (!string.IsNullOrEmpty(jobId))
            {
                Snackbar.Add($"Rescrape queued (job {jobId}).", Severity.Success);
                Nav?.NavigateTo(string.Format(JobDetailRouteTemplate, jobId));
            }
            else
                Snackbar.Add(RescrapeFailedMessage, Severity.Error);
        }
    }

    protected async Task RescrubAsync()
    {
        ArgumentNullException.ThrowIfNull(WriteService);
        ArgumentNullException.ThrowIfNull(Snackbar);
        if (Detail is not null)
        {
            var jobId = await WriteService.RescrubAsync(Detail.LibraryId, Detail.Version);
            if (!string.IsNullOrEmpty(jobId))
                Snackbar.Add($"Rescrub queued (job {jobId}).", Severity.Success);
            else
                Snackbar.Add(RescrubFailedMessage, Severity.Error);
        }
    }

    protected async Task DeleteVersionAsync()
    {
        ArgumentNullException.ThrowIfNull(DialogService);
        ArgumentNullException.ThrowIfNull(WriteService);
        ArgumentNullException.ThrowIfNull(Snackbar);
        ArgumentNullException.ThrowIfNull(Nav);
        if (Detail is not null)
        {
            var version = Detail.Version;
            var libraryId = Detail.LibraryId;
            var parameters = new DialogParameters
                {
                    {
                        nameof(ConfirmDialog.Message),
                        $"Delete version {version} of {libraryId}? This removes its chunks, pages, and audit log."
                    },
                    { nameof(ConfirmDialog.ConfirmLabel), DeleteConfirmLabel }
                };
            var dialog = await DialogService.ShowAsync<ConfirmDialog>($"Delete version {version}", parameters);
            var result = await dialog.Result;
            if (result is not null && !result.Canceled)
            {
                var ok = await WriteService.DeleteVersionAsync(libraryId, version);
                if (ok)
                {
                    Snackbar.Add($"Deleted version {version}.", Severity.Success);
                    Nav.NavigateTo(MonitorHomeRoute);
                }
                else
                    Snackbar.Add(DeleteFailedMessage, Severity.Error);
            }
        }
    }

    private const string RescrapeFailedMessage = "Rescrape failed (no prior scrape recorded).";
    private const string RescrubFailedMessage = "Rescrub failed.";
    private const string DeleteFailedMessage = "Delete failed.";
    private const string DeleteConfirmLabel = "Delete";
    private const string MonitorHomeRoute = "/monitor";
    private const string JobDetailRouteTemplate = "/monitor/jobs/{0}";
}
