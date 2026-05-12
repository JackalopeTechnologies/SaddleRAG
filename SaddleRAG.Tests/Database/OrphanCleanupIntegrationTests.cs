// OrphanCleanupIntegrationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Core.Models.Audit;
using SaddleRAG.Database;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Integration coverage for <c>GetDistinctLibraryVersionPairsAsync</c>
///     across every (LibraryId, Version)-keyed collection. Runs against a
///     live local MongoDB instance under the SaddleRAG_test_orphans database
///     and exercises the same Mongo group aggregation that backs the
///     cleanup_orphans MCP tool.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrphanCleanupIntegrationTests : IAsyncLifetime
{
    public OrphanCleanupIntegrationTests()
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

    public async ValueTask InitializeAsync()
    {
        await mContext.EnsureIndexesAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task PageRepoReturnsDistinctPairsAcrossInsertions()
    {
        var repo = new PageRepository(mContext);
        var libA = $"int-pages-a-{Guid.NewGuid():N}";
        var libB = $"int-pages-b-{Guid.NewGuid():N}";

        await repo.UpsertPageAsync(MakePage(libA, "1.0", "https://x.com/p1"),
                                   TestContext.Current.CancellationToken
                                  );
        await repo.UpsertPageAsync(MakePage(libA, "1.0", "https://x.com/p2"),
                                   TestContext.Current.CancellationToken
                                  );
        await repo.UpsertPageAsync(MakePage(libA, "2.0", "https://x.com/p3"),
                                   TestContext.Current.CancellationToken
                                  );
        await repo.UpsertPageAsync(MakePage(libB, "1.0", "https://x.com/p4"),
                                   TestContext.Current.CancellationToken
                                  );

        var pairs = await repo.GetDistinctLibraryVersionPairsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(new LibraryVersionKey(libA, "1.0"), pairs);
        Assert.Contains(new LibraryVersionKey(libA, "2.0"), pairs);
        Assert.Contains(new LibraryVersionKey(libB, "1.0"), pairs);
        var perLibA = pairs.Count(k => k.LibraryId == libA);
        var perLibB = pairs.Count(k => k.LibraryId == libB);
        Assert.Equal(expected: 2, perLibA);
        Assert.Equal(expected: 1, perLibB);

        await repo.DeleteAsync(libA, "1.0", TestContext.Current.CancellationToken);
        await repo.DeleteAsync(libA, "2.0", TestContext.Current.CancellationToken);
        await repo.DeleteAsync(libB, "1.0", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ChunkRepoReturnsDistinctPairsAcrossInsertions()
    {
        var repo = new ChunkRepository(mContext);
        var lib = $"int-chunks-{Guid.NewGuid():N}";

        await repo.InsertChunksAsync([
                                             MakeChunk(lib, "1.0"),
                                             MakeChunk(lib, "1.0"),
                                             MakeChunk(lib, "2.0")
                                         ],
                                     TestContext.Current.CancellationToken
                                    );

        var pairs = await repo.GetDistinctLibraryVersionPairsAsync(TestContext.Current.CancellationToken);
        var forLib = pairs.Where(k => k.LibraryId == lib).ToList();

        Assert.Contains(new LibraryVersionKey(lib, "1.0"), forLib);
        Assert.Contains(new LibraryVersionKey(lib, "2.0"), forLib);
        Assert.Equal(expected: 2, forLib.Count);

        await repo.DeleteChunksAsync(lib, "1.0", TestContext.Current.CancellationToken);
        await repo.DeleteChunksAsync(lib, "2.0", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LibraryProfileRepoReturnsDistinctPairsAcrossInsertions()
    {
        var repo = new LibraryProfileRepository(mContext);
        var lib = $"int-profiles-{Guid.NewGuid():N}";

        await repo.UpsertAsync(MakeProfile(lib, "1.0"), TestContext.Current.CancellationToken);
        await repo.UpsertAsync(MakeProfile(lib, "2.0"), TestContext.Current.CancellationToken);

        var pairs = await repo.GetDistinctLibraryVersionPairsAsync(TestContext.Current.CancellationToken);
        var forLib = pairs.Where(k => k.LibraryId == lib).ToList();

        Assert.Contains(new LibraryVersionKey(lib, "1.0"), forLib);
        Assert.Contains(new LibraryVersionKey(lib, "2.0"), forLib);
        Assert.Equal(expected: 2, forLib.Count);

        await repo.DeleteAsync(lib, "1.0", TestContext.Current.CancellationToken);
        await repo.DeleteAsync(lib, "2.0", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LibraryIndexRepoReturnsDistinctPairsAcrossInsertions()
    {
        var repo = new LibraryIndexRepository(mContext);
        var lib = $"int-indexes-{Guid.NewGuid():N}";

        await repo.UpsertAsync(MakeIndex(lib, "1.0"), TestContext.Current.CancellationToken);
        await repo.UpsertAsync(MakeIndex(lib, "2.0"), TestContext.Current.CancellationToken);

        var pairs = await repo.GetDistinctLibraryVersionPairsAsync(TestContext.Current.CancellationToken);
        var forLib = pairs.Where(k => k.LibraryId == lib).ToList();

        Assert.Contains(new LibraryVersionKey(lib, "1.0"), forLib);
        Assert.Contains(new LibraryVersionKey(lib, "2.0"), forLib);
        Assert.Equal(expected: 2, forLib.Count);

        await repo.DeleteAsync(lib, "1.0", TestContext.Current.CancellationToken);
        await repo.DeleteAsync(lib, "2.0", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Bm25ShardRepoReturnsDistinctPairsAcrossInsertions()
    {
        var repo = new Bm25ShardRepository(mContext);
        var lib = $"int-shards-{Guid.NewGuid():N}";

        await repo.ReplaceShardsAsync(lib,
                                      "1.0",
                                          [MakeShard(lib, "1.0", shardIndex: 0)],
                                      TestContext.Current.CancellationToken
                                     );
        await repo.ReplaceShardsAsync(lib,
                                      "2.0",
                                          [MakeShard(lib, "2.0", shardIndex: 0)],
                                      TestContext.Current.CancellationToken
                                     );

        var pairs = await repo.GetDistinctLibraryVersionPairsAsync(TestContext.Current.CancellationToken);
        var forLib = pairs.Where(k => k.LibraryId == lib).ToList();

        Assert.Contains(new LibraryVersionKey(lib, "1.0"), forLib);
        Assert.Contains(new LibraryVersionKey(lib, "2.0"), forLib);
        Assert.Equal(expected: 2, forLib.Count);

        await repo.DeleteAsync(lib, "1.0", TestContext.Current.CancellationToken);
        await repo.DeleteAsync(lib, "2.0", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExcludedSymbolsRepoReturnsDistinctPairsAcrossInsertions()
    {
        var repo = new ExcludedSymbolsRepository(mContext);
        var lib = $"int-excluded-{Guid.NewGuid():N}";

        await repo.UpsertManyAsync([
                                           MakeExcluded(lib, "1.0", "Foo"),
                                           MakeExcluded(lib, "1.0", "Bar"),
                                           MakeExcluded(lib, "2.0", "Foo")
                                       ],
                                   TestContext.Current.CancellationToken
                                  );

        var pairs = await repo.GetDistinctLibraryVersionPairsAsync(TestContext.Current.CancellationToken);
        var forLib = pairs.Where(k => k.LibraryId == lib).ToList();

        Assert.Contains(new LibraryVersionKey(lib, "1.0"), forLib);
        Assert.Contains(new LibraryVersionKey(lib, "2.0"), forLib);
        Assert.Equal(expected: 2, forLib.Count);

        await repo.DeleteAsync(lib, "1.0", TestContext.Current.CancellationToken);
        await repo.DeleteAsync(lib, "2.0", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ScrapeAuditRepoReturnsDistinctPairsAcrossInsertions()
    {
        var repo = new ScrapeAuditRepository(mContext);
        var lib = $"int-audit-{Guid.NewGuid():N}";

        await repo.InsertManyAsync([
                                           MakeAudit(lib, "1.0", "https://a.com/1"),
                                           MakeAudit(lib, "1.0", "https://a.com/2"),
                                           MakeAudit(lib, "2.0", "https://a.com/3")
                                       ],
                                   TestContext.Current.CancellationToken
                                  );

        var pairs = await repo.GetDistinctLibraryVersionPairsAsync(TestContext.Current.CancellationToken);
        var forLib = pairs.Where(k => k.LibraryId == lib).ToList();

        Assert.Contains(new LibraryVersionKey(lib, "1.0"), forLib);
        Assert.Contains(new LibraryVersionKey(lib, "2.0"), forLib);
        Assert.Equal(expected: 2, forLib.Count);

        await repo.DeleteByLibraryVersionAsync(lib, "1.0", TestContext.Current.CancellationToken);
        await repo.DeleteByLibraryVersionAsync(lib, "2.0", TestContext.Current.CancellationToken);
    }

    private static PageRecord MakePage(string libraryId, string version, string url) =>
        new PageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                LibraryId = libraryId,
                Version = version,
                Url = url,
                Title = "t",
                Category = DocCategory.HowTo,
                RawContent = "x",
                FetchedAt = DateTime.UtcNow,
                ContentHash = "h",
                Depth = 0
            };

    private static DocChunk MakeChunk(string libraryId, string version) =>
        new DocChunk
            {
                Id = Guid.NewGuid().ToString("N"),
                LibraryId = libraryId,
                Version = version,
                PageUrl = "https://x.com/page",
                PageTitle = "title",
                Category = DocCategory.HowTo,
                Content = "chunk content",
                TokenCount = 5
            };

    private static LibraryProfile MakeProfile(string libraryId, string version) =>
        new LibraryProfile
            {
                Id = LibraryProfileRepository.MakeId(libraryId, version),
                LibraryId = libraryId,
                Version = version,
                CreatedUtc = DateTime.UtcNow
            };

    private static LibraryIndex MakeIndex(string libraryId, string version) =>
        new LibraryIndex
            {
                Id = LibraryIndexRepository.MakeId(libraryId, version),
                LibraryId = libraryId,
                Version = version
            };

    private static Bm25Shard MakeShard(string libraryId, string version, int shardIndex) =>
        new Bm25Shard
            {
                Id = Bm25ShardRepository.MakeShardId(libraryId, version, shardIndex),
                LibraryId = libraryId,
                Version = version,
                ShardIndex = shardIndex
            };

    private static ExcludedSymbol MakeExcluded(string libraryId, string version, string name) =>
        new ExcludedSymbol
            {
                Id = ExcludedSymbol.MakeId(libraryId, version, name),
                LibraryId = libraryId,
                Version = version,
                Name = name,
                Reason = SymbolRejectionReason.GlobalStoplist,
                SampleSentences = [],
                ChunkCount = 1,
                CapturedUtc = DateTime.UtcNow
            };

    private static ScrapeAuditLogEntry MakeAudit(string libraryId, string version, string url) =>
        new ScrapeAuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                JobId = $"job-{Guid.NewGuid():N}",
                LibraryId = libraryId,
                Version = version,
                Url = url,
                Host = new Uri(url).Host,
                Depth = 0,
                DiscoveredAt = DateTime.UtcNow,
                Status = AuditStatus.Indexed
            };

    private const string TestConnectionString = "mongodb://localhost:27017";
    private const string TestDatabaseName = "SaddleRAG_test_orphans";
}
