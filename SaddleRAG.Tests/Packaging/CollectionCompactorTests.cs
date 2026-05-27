// CollectionCompactorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using SaddleRAG.Packaging;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class CollectionCompactorTests
{
    [Fact]
    public void DefaultHotCollectionsHasFourKnownEntries()
    {
        var compactor = new CollectionCompactor();

        Assert.Equal(4, compactor.DefaultHotCollections.Count);
        Assert.Contains("pages", compactor.DefaultHotCollections);
        Assert.Contains("chunks", compactor.DefaultHotCollections);
        Assert.Contains("scrape_audit_log", compactor.DefaultHotCollections);
        Assert.Contains("bm25Shards", compactor.DefaultHotCollections);
    }

    [Fact]
    public async Task GetStatsReturnsZeroStatsWhenCollectionThrows()
    {
        // When collStats raises MongoException (e.g. collection does not exist),
        // GetStatsAsync must return zero-valued stats rather than propagating.
        const string CollectionName = "nonexistent_test_collection";

        var fakeDatabase = Substitute.For<IMongoDatabase>();
        fakeDatabase
            .RunCommandAsync<BsonDocument>(Arg.Any<Command<BsonDocument>>(), Arg.Any<ReadPreference?>(),
                                          Arg.Any<CancellationToken>())
            .Returns<BsonDocument>(_ => throw new MongoException("ns not found"));

        var compactor = new CollectionCompactor();
        var stats = await compactor.GetStatsAsync(fakeDatabase, CollectionName,
                                                   TestContext.Current.CancellationToken);

        Assert.Equal(CollectionName, stats.Collection);
        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.Size);
        Assert.Equal(0, stats.StorageSize);
        Assert.Equal(0, stats.TotalIndexSize);
    }

    [Fact]
    public async Task GetStatsReturnsPopulatedStatsWhenCommandSucceeds()
    {
        const string CollectionName = "pages";
        const long ExpectedCount = 42;
        const long ExpectedSize = 100_000;
        const long ExpectedStorageSize = 80_000;
        const long ExpectedIndexSize = 20_000;

        var responseDoc = new BsonDocument
                              {
                                  { "count", ExpectedCount },
                                  { "size", ExpectedSize },
                                  { "storageSize", ExpectedStorageSize },
                                  { "totalIndexSize", ExpectedIndexSize },
                                  { "ok", 1 }
                              };

        var fakeDatabase = Substitute.For<IMongoDatabase>();
        fakeDatabase
            .RunCommandAsync<BsonDocument>(Arg.Any<Command<BsonDocument>>(), Arg.Any<ReadPreference?>(),
                                          Arg.Any<CancellationToken>())
            .Returns(responseDoc);

        var compactor = new CollectionCompactor();
        var stats = await compactor.GetStatsAsync(fakeDatabase, CollectionName,
                                                   TestContext.Current.CancellationToken);

        Assert.Equal(CollectionName, stats.Collection);
        Assert.Equal(ExpectedCount, stats.Count);
        Assert.Equal(ExpectedSize, stats.Size);
        Assert.Equal(ExpectedStorageSize, stats.StorageSize);
        Assert.Equal(ExpectedIndexSize, stats.TotalIndexSize);
    }
}
