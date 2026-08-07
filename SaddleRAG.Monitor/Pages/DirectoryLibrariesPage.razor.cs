// DirectoryLibrariesPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.AspNetCore.Components;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Scanning;
using SaddleRAG.Monitor.Services;

namespace SaddleRAG.Monitor.Pages;

/// <summary>Code-behind for explicit, user-controlled directory library actions.</summary>
public abstract class DirectoryLibrariesPageBase : ComponentBase
{
    [Inject]
    private IDirectoryLibraryMonitorDataService? DataService { get; set; }

    [Inject]
    private IDirectoryLibraryMonitorCommands? Commands { get; set; }

    protected IReadOnlyList<DirectoryLibraryMonitorRow> Rows { get; private set; } = [];

    protected DirectoryLibraryEditModel EditModel { get; } = new();

    protected DirectoryAccessTestResult? AccessResult { get; private set; }

    protected DirectoryRegistrationResult? RegistrationResult { get; private set; }

    protected DirectoryScanQueueResult? ScanResult { get; private set; }

    protected string? QueuedJobId { get; private set; }

    protected string? FailureMessage { get; private set; }

    protected string? BusyAction { get; private set; }

    protected bool IsBusy => BusyAction != null;

    protected DirectoryLibraryMonitorRow? SelectedRow =>
        Rows.FirstOrDefault(row => string.Equals(row.LibraryId,
                                                  EditModel.LibraryId,
                                                  StringComparison.OrdinalIgnoreCase));

    protected IReadOnlyList<DirectoryScanFileFailure> SelectedFailures =>
        SelectedRow?.FileFailures ?? [];

    protected override async Task OnInitializedAsync()
    {
        ArgumentNullException.ThrowIfNull(DataService);
        Rows = await DataService.ListAsync(profile: null);
        DirectoryLibraryMonitorRow? first = Rows.FirstOrDefault();
        if (first != null)
            SelectRow(first);
    }

    protected async Task TestAccessAsync()
    {
        ArgumentNullException.ThrowIfNull(Commands);
        ResetFeedback();
        if (string.IsNullOrWhiteSpace(EditModel.RootPath))
        {
            FailureMessage = RootPathRequiredMessage;
        }
        else
        {
            BusyAction = TestAction;
            try
            {
                AccessResult = await Commands.TestAccessAsync(EditModel.RootPath,
                                                              EditModel.Recursive);
            }
            catch(Exception)
            {
                FailureMessage = TestAccessFailureMessage;
            }
            finally
            {
                BusyAction = null;
            }
        }
    }

    protected async Task RegisterAsync()
    {
        ArgumentNullException.ThrowIfNull(Commands);
        ResetFeedback();
        if (string.IsNullOrWhiteSpace(EditModel.LibraryId)
            || string.IsNullOrWhiteSpace(EditModel.RootPath))
        {
            FailureMessage = RegistrationFieldsRequiredMessage;
        }
        else
        {
            BusyAction = RegisterAction;
            var request = new DirectoryRegistrationRequest(EditModel.LibraryId.Trim(),
                                                           EditModel.RootPath,
                                                           EditModel.Recursive,
                                                           EditModel.ExclusionPatterns,
                                                           EditModel.AllowedExtensions,
                                                           NormalizeOptional(EditModel.Name),
                                                           NormalizeOptional(EditModel.Hint));
            try
            {
                RegistrationResult = await Commands.RegisterAsync(request, profile: null);
            }
            catch(Exception)
            {
                FailureMessage = RegistrationFailureMessage;
            }
            finally
            {
                BusyAction = null;
            }
        }
    }

    protected async Task ScanAsync()
    {
        ArgumentNullException.ThrowIfNull(Commands);
        ResetFeedback();
        if (string.IsNullOrWhiteSpace(EditModel.LibraryId))
        {
            FailureMessage = LibraryIdRequiredMessage;
        }
        else
        {
            BusyAction = ScanAction;
            try
            {
                ScanResult = await Commands.ScanAsync(EditModel.LibraryId.Trim(), profile: null);
                QueuedJobId = ScanResult.JobId;
            }
            catch(Exception)
            {
                FailureMessage = ScanFailureMessage;
            }
            finally
            {
                BusyAction = null;
            }
        }
    }

    protected void SelectRow(DirectoryLibraryMonitorRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        EditModel.LibraryId = row.LibraryId;
        EditModel.Name = row.Name;
        EditModel.Hint = row.Hint;
        EditModel.RootPath = row.RootPath;
        EditModel.Recursive = row.Recursive;
        EditModel.AllowedExtensions = row.AllowedExtensions.ToArray();
        EditModel.ExclusionPatterns = row.ExclusionPatterns.ToArray();
        ResetFeedback();
    }

    protected static string FormatLastSuccessful(DirectoryLibraryMonitorRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string result = row.LastSuccessfulAt is DateTime timestamp
                            ? $"{row.LastSuccessfulVersion} · {timestamp.ToLocalTime():g}"
                            : NeverCompletedDisplay;
        return result;
    }

    protected static string FormatProgress(DirectoryLibraryMonitorRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string result = row.Progress == null
                            ? NoProgressDisplay
                            : $"{row.Progress.DocumentsCompleted} of {row.Progress.SupportedDocuments} documents";
        return result;
    }

    protected static double ProgressPercent(DirectoryScanJobProgress? progress)
    {
        double result = progress is { SupportedDocuments: > 0 }
                            ? progress.DocumentsCompleted * PercentScale / progress.SupportedDocuments
                            : 0;
        return result;
    }

    private void ResetFeedback()
    {
        AccessResult = null;
        RegistrationResult = null;
        ScanResult = null;
        FailureMessage = null;
    }

    private static string? NormalizeOptional(string value)
    {
        string? result = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return result;
    }

    private const double PercentScale = 100.0;
    private const string TestAction = "test";
    private const string RegisterAction = "register";
    private const string ScanAction = "scan";
    private const string RootPathRequiredMessage = "Enter the directory root before testing access.";
    private const string RegistrationFieldsRequiredMessage =
        "Library ID and root path are required before registration.";
    private const string LibraryIdRequiredMessage = "Select or enter a registered library before scanning.";
    private const string TestAccessFailureMessage = "The access test could not be completed.";
    private const string RegistrationFailureMessage = "The directory registration could not be completed.";
    private const string ScanFailureMessage = "The manual scan request could not be queued.";
    private const string NeverCompletedDisplay = "Not yet scanned";
    private const string NoProgressDisplay = "No recent scan";
}
