// InspectScrapeToolTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Text.Json;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Audit;

public sealed class InspectScrapeToolTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SummaryReturnsCountsAndBuckets()
    {
        var ctx = NewContext();
        await ctx.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(ctx);

        var jobId = $"job-{Guid.NewGuid():N}";
        await repo.InsertManyAsync(MakeMixedAudit(jobId, 100), TestContext.Current.CancellationToken);

        var factory = BuildTestFactory(ctx);

        var json = await InspectScrapeTool.InspectScrape(factory, jobId,
            status: null, skipReason: null, host: null, url: null, limit: 10,
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(jobId, root.GetProperty("JobId").GetString());
        Assert.Equal("summary", root.GetProperty("Mode").GetString());
        Assert.True(root.GetProperty("Summary").GetProperty("TotalConsidered").GetInt32() > 0);
        Assert.True(root.GetProperty("Summary").GetProperty("SkipReasonCounts").EnumerateObject().Any());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FilterByStatusReturnsMatchingEntries()
    {
        var ctx = NewContext();
        await ctx.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(ctx);

        var jobId = $"job-{Guid.NewGuid():N}";
        await repo.InsertManyAsync(MakeMixedAudit(jobId, 100), TestContext.Current.CancellationToken);
        var factory = BuildTestFactory(ctx);

        var json = await InspectScrapeTool.InspectScrape(factory, jobId,
            status: "Indexed", skipReason: null, host: null, url: null, limit: 200,
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("filter", root.GetProperty("Mode").GetString());
        foreach (var entry in root.GetProperty("Entries").EnumerateArray())
            Assert.Equal("Indexed", entry.GetProperty("Status").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FilterBySkipReasonReturnsMatchingEntries()
    {
        var ctx = NewContext();
        await ctx.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(ctx);
        var jobId = $"job-{Guid.NewGuid():N}";
        await repo.InsertManyAsync(MakeMixedAudit(jobId, 100), TestContext.Current.CancellationToken);
        var factory = BuildTestFactory(ctx);

        var json = await InspectScrapeTool.InspectScrape(factory, jobId,
            status: null, skipReason: "PatternExclude", host: null, url: null, limit: 200,
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("filter", root.GetProperty("Mode").GetString());
        foreach (var entry in root.GetProperty("Entries").EnumerateArray())
            Assert.Equal("PatternExclude", entry.GetProperty("SkipReason").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SingleUrlLookupReturnsEntry()
    {
        var ctx = NewContext();
        await ctx.EnsureIndexesAsync(TestContext.Current.CancellationToken);
        var repo = new ScrapeAuditRepository(ctx);
        var jobId = $"job-{Guid.NewGuid():N}";
        var targetUrl = "https://example.com/find-me";
        await repo.InsertManyAsync(new[]
        {
            new ScrapeAuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
                LibraryId = "lib",
                Version = "1.0",
                Url = targetUrl,
                ParentUrl = "https://example.com/",
                Host = "example.com",
                Depth = 1,
                DiscoveredAt = DateTime.UtcNow,
                Status = AuditStatus.Indexed
            }
        }, TestContext.Current.CancellationToken);
        var factory = BuildTestFactory(ctx);

        var json = await InspectScrapeTool.InspectScrape(factory, jobId,
            status: null, skipReason: null, host: null, url: targetUrl, limit: 50,
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("url", root.GetProperty("Mode").GetString());
        Assert.Equal("found", root.GetProperty("Status").GetString());
        Assert.Equal(targetUrl, root.GetProperty("Entry").GetProperty("Url").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UnknownJobIdReturnsNotFound()
    {
        var ctx = NewContext();
        var factory = BuildTestFactory(ctx);

        var json = await InspectScrapeTool.InspectScrape(factory, "no-such-job",
            status: "Indexed", skipReason: null, host: null, url: null, limit: 50,
            ct: TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("not_found", root.GetProperty("Status").GetString());
    }

    private static SaddleRagDbContext NewContext()
    {
        var settings = Options.Create(new SaddleRagDbSettings
        {
            ConnectionString = "mongodb://localhost:27017",
            DatabaseName = "SaddleRAG_test_audit"
        });
        return new SaddleRagDbContext(settings);
    }

    private static TestRepositoryFactory BuildTestFactory(SaddleRagDbContext ctx)
    {
        var repo = new ScrapeAuditRepository(ctx);
        return new TestRepositoryFactory(repo);
    }

    private static IEnumerable<ScrapeAuditLogEntry> MakeMixedAudit(string jobId, int count)
    {
        for (var i = 0; i < count; i++)
            yield return new ScrapeAuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = jobId,
                LibraryId = "lib",
                Version = "1.0",
                Url = $"https://example.com/{i}",
                Host = "example.com",
                Depth = 1,
                DiscoveredAt = DateTime.UtcNow,
                Status = (i % 4) switch
                {
                    0 => AuditStatus.Indexed,
                    1 => AuditStatus.Fetched,
                    2 => AuditStatus.Skipped,
                    _ => AuditStatus.Failed
                },
                SkipReason = (i % 4 == 2) ? AuditSkipReason.PatternExclude : null
            };
    }

    /// <summary>
    ///     Minimal <see cref="RepositoryFactory"/> subclass for tests that
    ///     returns a pre-constructed <see cref="IScrapeAuditRepository"/>
    ///     without requiring a live <see cref="SaddleRagDbContextFactory"/>.
    /// </summary>
    private sealed class TestRepositoryFactory : RepositoryFactory
    {
        public TestRepositoryFactory(IScrapeAuditRepository auditRepo)
            : base(BuildStubContextFactory())
        {
            mAuditRepo = auditRepo;
        }

        private readonly IScrapeAuditRepository mAuditRepo;

        public override IScrapeAuditRepository GetScrapeAuditRepository(string? profile = null)
        {
            return mAuditRepo;
        }

        private static SaddleRagDbContextFactory BuildStubContextFactory()
        {
            var settings = Options.Create(new SaddleRagDbSettings
            {
                ConnectionString = "mongodb://localhost:27017",
                DatabaseName = "SaddleRAG_test_audit"
            });
            return new SaddleRagDbContextFactory(settings);
        }
    }
}
