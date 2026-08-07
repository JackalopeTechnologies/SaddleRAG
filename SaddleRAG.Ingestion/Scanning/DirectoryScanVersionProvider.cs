// DirectoryScanVersionProvider.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Globalization;
using SaddleRAG.Core.Enums;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>
///     Captures the user-visible local calendar date once when a manual scan
///     is queued and decides whether a terminal same-date attempt may run.
/// </summary>
public sealed class DirectoryScanVersionProvider
{
    public DirectoryScanVersionProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        mTimeProvider = timeProvider;
    }

    private readonly TimeProvider mTimeProvider;

    public DirectoryScanVersion Capture()
    {
        var queuedAt = mTimeProvider.GetLocalNow();
        var value = queuedAt.ToString(VersionFormat, CultureInfo.InvariantCulture);
        var result = new DirectoryScanVersion(value, queuedAt);
        return result;
    }

    public static DirectoryScanDecision DecideSameDate(DocumentRevisionState revisionState)
    {
        var result = revisionState switch
                         {
                             DocumentRevisionState.Published =>
                                 new DirectoryScanDecision(ShouldScan: false, AlreadyScannedTodayStatus),
                             DocumentRevisionState.Failed or DocumentRevisionState.Cancelled =>
                                 new DirectoryScanDecision(ShouldScan: true, RetryAllowedStatus),
                             _ => new DirectoryScanDecision(ShouldScan: false, ScanInProgressStatus)
                         };
        return result;
    }

    public const string AlreadyScannedTodayStatus = "ALREADY_SCANNED_TODAY";
    public const string RetryAllowedStatus = "RETRY_ALLOWED";
    public const string ScanInProgressStatus = "SCAN_IN_PROGRESS";

    private const string VersionFormat = "yyyy-MM-dd";
}
