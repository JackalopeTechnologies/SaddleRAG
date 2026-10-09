// MongoClientCacheTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using MongoDB.Driver;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

public sealed class MongoClientCacheTests
{
    [Fact]
    public void AFailedClientCreationIsRetriedOnTheNextCallThenCached()
    {
        string connectionString = $"mongodb+srv://cache-test-{Guid.NewGuid():N}.example";
        var created = new CachedMongoClient(Substitute.For<IMongoClient>(),
                                            new DatabaseReachabilityTracker(TimeProvider.System)
                                           );
        int calls = 0;

        CachedMongoClient Create(string _)
        {
            calls++;
            if (calls == 1)
                throw new MongoConfigurationException("The DNS lookup for the SRV record failed.");
            return created;
        }

        Assert.Throws<MongoConfigurationException>(() => MongoClientCache.GetOrCreate(connectionString, Create));
        Assert.Same(created, MongoClientCache.GetOrCreate(connectionString, Create));
        Assert.Same(created, MongoClientCache.GetOrCreate(connectionString, Create));
        Assert.Equal(expected: 2, calls);
    }
}
