// ScrapeAuditCleanupIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Audit;

public sealed class ScrapeAuditCleanupIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeleteByLibraryVersionRemovesPriorAuditRows()
    {
        var settings = Options.Create(new SaddleRagDbSettings
        {
            ConnectionString = "mongodb://localhost:27017",
            DatabaseName = TestDatabaseName
        });
        var ctx = new SaddleRagDbContext(settings);
        await ctx.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(ctx);

        var lib = $"lib-cleanup-{Guid.NewGuid():N}";
        var jobId = $"job-{Guid.NewGuid():N}";

        await repo.InsertManyAsync(new[]
        {
            new ScrapeAuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
                LibraryId = lib,
                Version = "1.0",
                Url = "https://x.com/1",
                Host = "x.com",
                Depth = 0,
                DiscoveredAt = DateTime.UtcNow,
                Status = AuditStatus.Indexed
            },
            new ScrapeAuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
                LibraryId = lib,
                Version = "1.0",
                Url = "https://x.com/2",
                Host = "x.com",
                Depth = 1,
                DiscoveredAt = DateTime.UtcNow,
                Status = AuditStatus.Skipped,
                SkipReason = AuditSkipReason.PatternExclude
            }
        }, TestContext.Current.CancellationToken);

        var deleted = await repo.DeleteByLibraryVersionAsync(lib, "1.0",
                                                              TestContext.Current.CancellationToken);

        Assert.Equal(2L, deleted);
        var remaining = await repo.QueryAsync(jobId, null, null, null, null, 100,
                                              TestContext.Current.CancellationToken);
        Assert.Empty(remaining);
    }

    private const string TestDatabaseName = "SaddleRAG_test_audit";
}
