// DirectoryScanReportTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

public sealed class DirectoryScanReportTests
{
    [Fact]
    public void ReportShapeCannotExposeTheSelectedRootOrPreviewVersion()
    {
        var properties = typeof(DirectoryScanReport).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("RootPath", properties);
        Assert.DoesNotContain("Version", properties);
    }

    [Fact]
    public void SerializedReportContainsOnlyRelativePathsAndSanitizedDetail()
    {
        const string SecretRoot = "C:\\private\\manuals";
        var report = new DirectoryScanReport
                         {
                             LibraryId = "manual-library",
                             ScanRunId = "scan-run",
                             Status = DirectoryScanStatus.CompletedWithErrors,
                             ReasonCode = DirectoryScanReasonCodes.ScanCompletedWithErrors,
                             Detail = "One or more entries could not be previewed.",
                             StartedAtUtc = ScanTime,
                             CompletedAtUtc = ScanTime,
                             Entries =
                             [
                                 new DirectoryScanEntryResult("locked.pdf",
                                                              DirectoryScanEntryKind.File,
                                                              DirectoryScanEntryStatus.Failed,
                                                              DirectoryScanReasonCodes.FileLocked,
                                                              "The file is locked.",
                                                              0,
                                                              10)
                             ],
                             DiscoveredCount = 1,
                             FailedCount = 1
                         };

        var json = JsonSerializer.Serialize(report);

        Assert.Contains("locked.pdf", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretRoot, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReasonCodesAreUniqueStableUppercaseValues()
    {
        var values = typeof(DirectoryScanReasonCodes).GetFields()
                                                         .Where(f => f.IsLiteral)
                                                         .Select(f => Assert.IsType<string>(f.GetRawConstantValue()))
                                                         .ToArray();

        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(values, value => Assert.Matches("^[A-Z0-9_]+$", value));
    }

    private static readonly DateTime ScanTime = new(year: 2026,
                                                    month: 8,
                                                    day: 4,
                                                    hour: 12,
                                                    minute: 0,
                                                    second: 0,
                                                    DateTimeKind.Utc);
}
