// AuditEnumStabilityTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using SaddleRAG.Core.Models.Audit;

#endregion

namespace SaddleRAG.Tests.Audit;

public sealed class AuditEnumStabilityTests
{
    [Fact]
    public void AuditStatusValuesArePinned()
    {
        Assert.Equal(expected: 0, (int) AuditStatus.Considered);
        Assert.Equal(expected: 1, (int) AuditStatus.Skipped);
        Assert.Equal(expected: 2, (int) AuditStatus.Fetched);
        Assert.Equal(expected: 3, (int) AuditStatus.Failed);
        Assert.Equal(expected: 4, (int) AuditStatus.Indexed);
    }

    [Fact]
    public void AuditSkipReasonValuesArePinned()
    {
        Assert.Equal(expected: 0, (int) AuditSkipReason.PatternExclude);
        Assert.Equal(expected: 1, (int) AuditSkipReason.PatternMissAllowed);
        Assert.Equal(expected: 2, (int) AuditSkipReason.BinaryExt);
        Assert.Equal(expected: 3, (int) AuditSkipReason.OffSiteDepth);
        Assert.Equal(expected: 4, (int) AuditSkipReason.SameHostDepth);
        Assert.Equal(expected: 5, (int) AuditSkipReason.HostGated);
        Assert.Equal(expected: 6, (int) AuditSkipReason.AlreadyVisited);
        Assert.Equal(expected: 7, (int) AuditSkipReason.QueueLimit);
    }

    [Fact]
    public void ScrapeAuditLogEntryRoundTripsThroughJson()
    {
        var entry = new ScrapeAuditLogEntry
                        {
                            Id = "abc-123",
                            JobId = "job-1",
                            LibraryId = "lib",
                            Version = "1.0",
                            Url = "https://example.com/a",
                            ParentUrl = "https://example.com/",
                            Host = "example.com",
                            Depth = 1,
                            DiscoveredAt = new DateTime(year: 2026,
                                                        month: 5,
                                                        day: 2,
                                                        hour: 10,
                                                        minute: 0,
                                                        second: 0,
                                                        DateTimeKind.Utc
                                                       ),
                            Status = AuditStatus.Skipped,
                            SkipReason = AuditSkipReason.OffSiteDepth,
                            SkipDetail = "depth=2 limit=1",
                            PageOutcome = null
                        };

        var json = JsonSerializer.Serialize(entry);
        var roundTrip = JsonSerializer.Deserialize<ScrapeAuditLogEntry>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(entry.Id, roundTrip.Id);
        Assert.Equal(entry.Status, roundTrip.Status);
        Assert.Equal(entry.SkipReason, roundTrip.SkipReason);
    }
}
