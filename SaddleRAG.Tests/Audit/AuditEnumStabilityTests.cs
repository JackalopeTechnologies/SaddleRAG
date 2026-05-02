// AuditEnumStabilityTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models.Audit;

#endregion

namespace SaddleRAG.Tests.Audit;

public sealed class AuditEnumStabilityTests
{
    [Fact]
    public void AuditStatusValuesArePinned()
    {
        Assert.Equal(0, (int) AuditStatus.Considered);
        Assert.Equal(1, (int) AuditStatus.Skipped);
        Assert.Equal(2, (int) AuditStatus.Fetched);
        Assert.Equal(3, (int) AuditStatus.Failed);
        Assert.Equal(4, (int) AuditStatus.Indexed);
    }

    [Fact]
    public void AuditSkipReasonValuesArePinned()
    {
        Assert.Equal(0, (int) AuditSkipReason.PatternExclude);
        Assert.Equal(1, (int) AuditSkipReason.PatternMissAllowed);
        Assert.Equal(2, (int) AuditSkipReason.BinaryExt);
        Assert.Equal(3, (int) AuditSkipReason.OffSiteDepth);
        Assert.Equal(4, (int) AuditSkipReason.SameHostDepth);
        Assert.Equal(5, (int) AuditSkipReason.HostGated);
        Assert.Equal(6, (int) AuditSkipReason.AlreadyVisited);
        Assert.Equal(7, (int) AuditSkipReason.QueueLimit);
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
            DiscoveredAt = new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc),
            Status = AuditStatus.Skipped,
            SkipReason = AuditSkipReason.OffSiteDepth,
            SkipDetail = "depth=2 limit=1",
            PageOutcome = null
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<ScrapeAuditLogEntry>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(entry.Id, roundTrip.Id);
        Assert.Equal(entry.Status, roundTrip.Status);
        Assert.Equal(entry.SkipReason, roundTrip.SkipReason);
    }
}
