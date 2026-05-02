// ScrapeAuditRepositoryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

#endregion

namespace SaddleRAG.Tests.Audit;

/// <summary>
///     Verifies that EnsureIndexesAsync creates the required compound indexes
///     on the scrape_audit_log collection against a live local MongoDB instance.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScrapeAuditRepositoryTests
{
    private const string TestDatabaseName = "SaddleRAG_test_audit";

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
}
