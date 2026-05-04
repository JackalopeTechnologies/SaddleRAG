// ScrapeAuditRepositoryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Audit;

/// <summary>
///     Verifies that EnsureIndexesAsync creates the required compound indexes
///     on the scrape_audit_log collection against a live local MongoDB instance.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScrapeAuditRepositoryTests
{
    public ScrapeAuditRepositoryTests()
    {
        var settings = Options.Create(new SaddleRagDbSettings
                                          {
                                              ConnectionString = "mongodb://localhost:27017",
                                              DatabaseName = TestDatabaseName
                                          }
                                     );
        mContext = new SaddleRagDbContext(settings);
    }

    private readonly SaddleRagDbContext mContext;

    [Fact]
    public async Task EnsureIndexesAsyncCreatesAuditIndexes()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var cursor = await mContext.ScrapeAuditLog.Indexes.ListAsync(ct);
        var indexes = await cursor.ToListAsync(ct);

        // Three compound indexes plus Mongo's implicit _id index = 4 total
        Assert.True(indexes.Count >= 4);
        Assert.Contains(indexes, i => i["name"].AsString.Contains("JobId_1_Status_1"));
        Assert.Contains(indexes, i => i["name"].AsString.Contains("JobId_1_Host_1"));
        Assert.Contains(indexes, i => i["name"].AsString.Contains("JobId_1_Url_1"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InsertManyAndQueryByJobIdRoundTrips()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(mContext);
        var jobId = $"test-{Guid.NewGuid():N}";

        var entries = new[]
                          {
                              MakeEntry(jobId, "https://a.com/1", AuditStatus.Indexed),
                              MakeEntry(jobId, "https://a.com/2", AuditStatus.Skipped, AuditSkipReason.PatternExclude),
                              MakeEntry(jobId, "https://b.com/1", AuditStatus.Fetched)
                          };

        await repo.InsertManyAsync(entries, TestContext.Current.CancellationToken);

        var fetched = await repo.QueryAsync(jobId,
                                            status: null,
                                            skipReason: null,
                                            host: null,
                                            urlSubstring: null,
                                            limit: 100,
                                            TestContext.Current.CancellationToken
                                           );

        Assert.Equal(expected: 3, fetched.Count);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DeleteByLibraryVersionRemovesPriorAudit()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(mContext);
        var jobId = $"test-{Guid.NewGuid():N}";
        var lib = $"lib-{Guid.NewGuid():N}";

        await repo.InsertManyAsync(new[] { MakeEntry(jobId, "https://a.com/", AuditStatus.Indexed, libraryId: lib) },
                                   TestContext.Current.CancellationToken
                                  );

        var removed = await repo.DeleteByLibraryVersionAsync(lib, "1.0", TestContext.Current.CancellationToken);

        Assert.True(removed >= 1);
        var remaining = await repo.QueryAsync(jobId,
                                              status: null,
                                              skipReason: null,
                                              host: null,
                                              urlSubstring: null,
                                              limit: 100,
                                              TestContext.Current.CancellationToken
                                             );
        Assert.Empty(remaining);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SummarizeReturnsBucketedCounts()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(mContext);
        var jobId = $"test-{Guid.NewGuid():N}";

        var entries = new[]
                          {
                              MakeEntry(jobId, "https://a.com/1", AuditStatus.Indexed),
                              MakeEntry(jobId, "https://a.com/2", AuditStatus.Indexed),
                              MakeEntry(jobId, "https://a.com/3", AuditStatus.Skipped, AuditSkipReason.OffSiteDepth),
                              MakeEntry(jobId, "https://a.com/4", AuditStatus.Skipped, AuditSkipReason.OffSiteDepth),
                              MakeEntry(jobId, "https://a.com/5", AuditStatus.Skipped, AuditSkipReason.PatternExclude)
                          };
        await repo.InsertManyAsync(entries, TestContext.Current.CancellationToken);

        var summary = await repo.SummarizeAsync(jobId, TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, summary.IndexedCount);
        Assert.Equal(expected: 3, summary.SkippedCount);
        Assert.Equal(expected: 2, summary.SkipReasonCounts[AuditSkipReason.OffSiteDepth]);
        Assert.Equal(expected: 1, summary.SkipReasonCounts[AuditSkipReason.PatternExclude]);
    }

    private static ScrapeAuditLogEntry MakeEntry(string jobId,
                                                 string url,
                                                 AuditStatus status,
                                                 AuditSkipReason? skip = null,
                                                 string libraryId = "lib-x")
    {
        var uri = new Uri(url);
        return new ScrapeAuditLogEntry
                   {
                       Id = Guid.NewGuid().ToString("N"),
                       JobId = jobId,
                       LibraryId = libraryId,
                       Version = "1.0",
                       Url = url,
                       Host = uri.Host,
                       Depth = 1,
                       DiscoveredAt = DateTime.UtcNow,
                       Status = status,
                       SkipReason = skip
                   };
    }

    private const string TestDatabaseName = "SaddleRAG_test_audit";
}
