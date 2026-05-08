// RetentionTtlIndexTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Verifies that <see cref="SaddleRagDbContext.EnsureIndexesAsync" />
///     creates the 30-day TTL indexes on ScrapeAuditLog (DiscoveredAt),
///     ScrapeJobs (CompletedAt), BackgroundJobs (CompletedAt), and
///     RescrubJobs (CompletedAt). The TTL is the safety net behind the
///     manual cleanup tools — a regression here would let job and audit
///     debris accumulate forever.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RetentionTtlIndexTests
{
    public RetentionTtlIndexTests()
    {
        var settings = Options.Create(new SaddleRagDbSettings
                                          {
                                              ConnectionString = TestConnectionString,
                                              DatabaseName = TestDatabaseName
                                          }
                                     );
        mContext = new SaddleRagDbContext(settings);
    }

    private readonly SaddleRagDbContext mContext;

    [Fact]
    public async Task ScrapeAuditLogHasTtlIndexOnDiscoveredAt()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

        var ttl = await FindTtlIndexAsync(mContext.ScrapeAuditLog.Indexes,
                                          ScrapeAuditFieldDiscoveredAt,
                                          TestContext.Current.CancellationToken
                                         );

        Assert.NotNull(ttl);
        Assert.Equal(expected: ExpectedRetentionSeconds, ttl[BsonFieldExpireAfterSeconds].ToInt64());
    }

    [Fact]
    public async Task ScrapeJobsHasTtlIndexOnCompletedAt()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

        var ttl = await FindTtlIndexAsync(mContext.ScrapeJobs.Indexes,
                                          JobFieldCompletedAt,
                                          TestContext.Current.CancellationToken
                                         );

        Assert.NotNull(ttl);
        Assert.Equal(expected: ExpectedRetentionSeconds, ttl[BsonFieldExpireAfterSeconds].ToInt64());
    }

    [Fact]
    public async Task BackgroundJobsHasTtlIndexOnCompletedAt()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

        var ttl = await FindTtlIndexAsync(mContext.BackgroundJobs.Indexes,
                                          JobFieldCompletedAt,
                                          TestContext.Current.CancellationToken
                                         );

        Assert.NotNull(ttl);
        Assert.Equal(expected: ExpectedRetentionSeconds, ttl[BsonFieldExpireAfterSeconds].ToInt64());
    }

    [Fact]
    public async Task RescrubJobsHasTtlIndexOnCompletedAt()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);

        var ttl = await FindTtlIndexAsync(mContext.RescrubJobs.Indexes,
                                          JobFieldCompletedAt,
                                          TestContext.Current.CancellationToken
                                         );

        Assert.NotNull(ttl);
        Assert.Equal(expected: ExpectedRetentionSeconds, ttl[BsonFieldExpireAfterSeconds].ToInt64());
    }

    private static async Task<BsonDocument?> FindTtlIndexAsync<T>(IMongoIndexManager<T> indexes,
                                                                  string keyField,
                                                                  CancellationToken ct)
    {
        var cursor = await indexes.ListAsync(ct);
        var all = await cursor.ToListAsync(ct);
        var result = all.FirstOrDefault(i => i.Contains(BsonFieldExpireAfterSeconds)
                                          && i[BsonFieldKey].AsBsonDocument.Contains(keyField)
                                        );
        return result;
    }

    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string TestDatabaseName = "SaddleRAG_test_ttl";
    private const string BsonFieldKey = "key";
    private const string BsonFieldExpireAfterSeconds = "expireAfterSeconds";
    private const string ScrapeAuditFieldDiscoveredAt = "DiscoveredAt";
    private const string JobFieldCompletedAt = "CompletedAt";
    private const int RetentionDays = 30;
    private const int SecondsPerDay = 86_400;
    private const long ExpectedRetentionSeconds = RetentionDays * SecondsPerDay;
}
