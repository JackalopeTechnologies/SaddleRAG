// LocalEndpointTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Hosting;

namespace SaddleRAG.Tests.Core;

public sealed class LocalEndpointTests
{
    [Theory]
    [InlineData("mongodb://localhost:27017", true)]
    [InlineData("mongodb://user:p%40ss@127.0.0.1:27017/SaddleRAG?authSource=admin", true)]
    [InlineData("mongodb://[::1]:27017", true)]
    [InlineData("mongodb://localhost:27017,db2.lan:27017/?replicaSet=rs0", false)]
    [InlineData("mongodb+srv://cluster0.example.net", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLoopbackMongoConnectionString(string? connectionString, bool expected)
    {
        Assert.Equal(expected, LocalEndpoint.IsLoopbackMongoConnectionString(connectionString));
    }

    [Theory]
    [InlineData("mongodb://LOCALHOST", true)]
    [InlineData("mongodb://localhost,127.0.0.1:27018,[::1]:27019", true)]
    [InlineData("mongodb://us@er:pa@ss@localhost:27017", true)]
    [InlineData("mongodb://localhost?directConnection=true", true)]
    [InlineData("mongodb://", false)]
    [InlineData("mongodb://@", false)]
    [InlineData("mongodb://remote.example.test@localhost", true)]
    [InlineData("mongodb://localhost@remote.example.test", false)]
    [InlineData("http://localhost", false)]
    [InlineData("localhost:27017", false)]
    [InlineData("mongodb://localhost.example.test", false)]
    public void IsLoopbackMongoConnectionStringHandlesHostListEdgeCases(string connectionString, bool expected)
    {
        Assert.Equal(expected, LocalEndpoint.IsLoopbackMongoConnectionString(connectionString));
    }
}
