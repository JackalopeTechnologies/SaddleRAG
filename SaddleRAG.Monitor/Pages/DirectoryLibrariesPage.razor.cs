// DirectoryLibrariesPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.AspNetCore.Components;
using MudBlazor;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Ingestion.Documents.Docling;
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

    [Inject]
    private IDoclingCapabilityService? Capability { get; set; }

    /// <summary>
    ///     Last observed document-scanner status. Read from the cached
    ///     <see cref="IDoclingCapabilityService.CurrentStatus" /> so opening this page never
    ///     probes a user-managed endpoint; <see cref="RecheckScannerAsync" /> is the explicit
    ///     way to force one.
    /// </summary>
    protected DoclingCapabilityStatus ScannerStatus { get; private set; } = smUncheckedScanner;

    /// <summary>
    ///     True when a scan of the selected library would be refused: the document scanner
    ///     is not ready and this library accepts a format only the scanner can convert.
    ///     A library restricted to text, Markdown, or HTML never needs the scanner and is
    ///     never blocked by it.
    /// </summary>
    protected bool ScanBlocked => ScannerStatus.State != DoclingCapabilityState.Ready
                                  && AcceptsScannerFormats();

    protected string? ScanRecoveryInstallUrl { get; private set; }

    protected string? ScanRecoveryReleaseUrl { get; private set; }

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
        ArgumentNullException.ThrowIfNull(Capability);
        ScannerStatus = Capability.CurrentStatus;
        Rows = await DataService.ListAsync(profile: null);
        DirectoryLibraryMonitorRow? first = Rows.FirstOrDefault();
        if (first != null)
            SelectRow(first);
    }

    /// <summary>
    ///     Explicit operator action: probe the user-managed endpoint once and adopt the
    ///     answer. SaddleRAG still neither starts nor configures the scanner.
    /// </summary>
    protected async Task RecheckScannerAsync()
    {
        ArgumentNullException.ThrowIfNull(Capability);
        ResetFeedback();
        BusyAction = RecheckAction;
        try
        {
            ScannerStatus = await Capability.GetStatusAsync(refresh: true);
        }
        catch(Exception)
        {
            FailureMessage = RecheckFailureMessage;
        }
        finally
        {
            BusyAction = null;
        }
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
        string? refusal = ResolveScanRefusal();
        if (refusal == null)
            await QueueScanAsync();
        else
            ReportRefusal(refusal);
    }

    /// <summary>
    ///     Why this press cannot be honoured, or null when it can. A scan blocked by the
    ///     document scanner is refused here rather than queued and rejected downstream:
    ///     the scan runner's preflight would fail it anyway, and waiting for that answer
    ///     is what made the failure look like a hang.
    /// </summary>
    private string? ResolveScanRefusal() =>
        (string.IsNullOrWhiteSpace(EditModel.LibraryId), ScanBlocked) switch
            {
                (true, var _) => LibraryIdRequiredMessage,
                (var _, true) => DescribeScannerBlock(),
                var _ => null
            };

    private void ReportRefusal(string refusal)
    {
        FailureMessage = refusal;
        if (ScanBlocked)
        {
            ScanRecoveryInstallUrl = DoclingInstallInstructions.OfficialInstallUrl;
            ScanRecoveryReleaseUrl = DoclingInstallInstructions.OfficialReleaseUrl;
        }
    }

    private async Task QueueScanAsync()
    {
        ArgumentNullException.ThrowIfNull(Commands);
        BusyAction = ScanAction;
        try
        {
            ScanResult = await Commands.ScanAsync(EditModel.LibraryId.Trim(), profile: null);
            QueuedJobId = ScanResult.JobId;
            AdoptRefusedScan(ScanResult);
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

    /// <summary>
    ///     A queue request can still come back refused — the scanner may have dropped
    ///     between the last status read and the press. Reporting it as informational
    ///     progress hid both the reason and the way out, so a refusal is surfaced with
    ///     the observed reason code, its detail, and the official recovery links.
    /// </summary>
    private void AdoptRefusedScan(DirectoryScanQueueResult result)
    {
        if (string.Equals(result.Status, DirectoryScanQueueStatuses.Failed, StringComparison.Ordinal))
        {
            FailureMessage = string.IsNullOrWhiteSpace(result.Detail)
                                 ? $"{ScanRefusedPrefix} ({result.ReasonCode})"
                                 : $"{ScanRefusedPrefix} ({result.ReasonCode}) {result.Detail}";
            ScanRecoveryInstallUrl = result.OfficialInstallUrl;
            ScanRecoveryReleaseUrl = result.OfficialReleaseUrl;
        }
    }

    private string DescribeScannerBlock()
    {
        string detail = string.IsNullOrWhiteSpace(ScannerStatus.Detail)
                            ? ScannerStatus.State.ToString()
                            : ScannerStatus.Detail;
        return $"{ScannerBlockedPrefix} ({ScannerStatus.ReasonCode}) {detail}";
    }

    private bool AcceptsScannerFormats()
    {
        IReadOnlyList<string> allowed = EditModel.AllowedExtensions;
        // An empty allow-list means "every supported extension", which includes the
        // scanner-only formats.
        bool result = allowed.Count == 0
                      || allowed.Any(extension => smScannerFormats.Contains(NormalizeExtension(extension)));
        return result;
    }

    private static string NormalizeExtension(string extension)
    {
        string trimmed = extension.Trim();
        return trimmed.StartsWith('.') ? trimmed : $".{trimmed}";
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

    /// <summary>
    ///     What the scan walked versus what it can actually convert. The gap between the
    ///     two is the usual explanation for a scan that looks like it skipped most of a folder.
    /// </summary>
    public static string FormatDiscovery(DirectoryScanJobProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return $"{progress.FilesDiscovered} files walked · {progress.SupportedDocuments} convertible · "
               + $"{progress.DocumentsCompleted} done";
    }

    /// <summary>
    ///     When the latest scan started and when it last moved. A running job that has not
    ///     moved for a long time is the case "Running" on its own hid completely.
    /// </summary>
    public static string FormatJobTiming(DirectoryLibraryMonitorRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return (row.LatestJobStartedAt, row.LatestJobLastProgressAt) switch
            {
                (null, DateTime progressed) => $"Queued · last progress {DescribeAge(progressed)}",
                (DateTime started, null) => $"Started {DescribeAge(started)} · no progress recorded",
                (DateTime started, DateTime progressed) =>
                    $"Started {DescribeAge(started)} · last progress {DescribeAge(progressed)}",
                var _ => NoJobTimingDisplay
            };
    }

    /// <summary>Colour for the job-status chip; anything unfinished stays informational.</summary>
    public static Color JobColor(string? status) => status switch
        {
            nameof(JobStatus.Failed) => Color.Error,
            nameof(JobStatus.Completed) => Color.Success,
            nameof(JobStatus.Cancelled) => Color.Warning,
            nameof(JobStatus.Running) => Color.Info,
            var _ => Color.Default
        };

    /// <summary>Colour for the document-scanner state chip.</summary>
    protected Color ScannerColor => ScannerStatus.State switch
        {
            DoclingCapabilityState.Ready => Color.Success,
            DoclingCapabilityState.Starting => Color.Info,
            DoclingCapabilityState.Unavailable => Color.Error,
            var _ => Color.Default
        };

    protected string FormatScannerCheckedAt() =>
        ScannerStatus.LastCheckedAt == DateTimeOffset.MinValue
            ? NeverCheckedDisplay
            : $"checked {DescribeAge(ScannerStatus.LastCheckedAt.UtcDateTime)}";

    private static string DescribeAge(DateTime timestamp)
    {
        TimeSpan age = DateTime.UtcNow - timestamp;
        return age switch
            {
                { TotalSeconds: < SecondsPerMinute } => JustNowDisplay,
                { TotalMinutes: < MinutesPerHour } => $"{(int) age.TotalMinutes} min ago",
                { TotalHours: < HoursPerDay } => $"{(int) age.TotalHours} h ago",
                var _ => $"{(int) age.TotalDays} d ago"
            };
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
        ScanRecoveryInstallUrl = null;
        ScanRecoveryReleaseUrl = null;
    }

    private static string? NormalizeOptional(string value)
    {
        string? result = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return result;
    }

    /// <summary>Formats only the document scanner can convert.</summary>
    private static readonly HashSet<string> smScannerFormats =
        new(StringComparer.OrdinalIgnoreCase) { DirectoryScanLimits.PdfExtension, DirectoryScanLimits.DocxExtension };

    /// <summary>Stands in until the first cached status read, so the view never sees null.</summary>
    private static readonly DoclingCapabilityStatus smUncheckedScanner =
        new(DoclingCapabilityState.NotChecked,
            DoclingReasonCodes.NotConfigured,
            UncheckedScannerDetail,
            string.Empty,
            DateTimeOffset.MinValue,
            string.Empty);

    private const double PercentScale = 100.0;
    private const int SecondsPerMinute = 60;
    private const int MinutesPerHour = 60;
    private const int HoursPerDay = 24;
    private const string JustNowDisplay = "just now";
    private const string NoJobTimingDisplay = "No scan has run yet";
    private const string NeverCheckedDisplay = "not checked yet";
    private const string TestAction = "test";
    private const string RegisterAction = "register";
    private const string ScanAction = "scan";
    private const string RecheckAction = "recheck";
    private const string RootPathRequiredMessage = "Enter the directory root before testing access.";
    private const string RegistrationFieldsRequiredMessage =
        "Library ID and root path are required before registration.";
    private const string LibraryIdRequiredMessage = "Select or enter a registered library before scanning.";
    private const string TestAccessFailureMessage = "The access test could not be completed.";
    private const string RegistrationFailureMessage = "The directory registration could not be completed.";
    private const string ScanFailureMessage = "The manual scan request could not be queued.";
    private const string RecheckFailureMessage = "The document scanner status could not be read.";
    private const string ScannerBlockedPrefix =
        "This library accepts PDF or DOCX and the document scanner is not ready, so scanning is held:";
    private const string ScanRefusedPrefix = "The document scanner refused this scan:";
    private const string UncheckedScannerDetail = "The document scanner has not been checked yet.";
    private const string NeverCompletedDisplay = "Not yet scanned";
    private const string NoProgressDisplay = "No recent scan";
}
