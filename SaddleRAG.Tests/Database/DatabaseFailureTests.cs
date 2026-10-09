// DatabaseFailureTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

public sealed class DatabaseFailureTests
{
    [Fact]
    public void AsUnavailableReturnsADatabaseUnavailableExceptionUnchanged()
    {
        var unavailable = new DatabaseUnavailableException("localhost:27017", innerException: null);

        Assert.Same(unavailable, DatabaseFailure.AsUnavailable(unavailable, smNoneUnreachable));
    }

    [Fact]
    public void AsUnavailableNamesTheEndpointOfAFailedConnection()
    {
        var connectionFailure = new MongoConnectionException(smConnectionId, "An exception occurred while opening a connection to the server.");

        var unavailable = DatabaseFailure.AsUnavailable(connectionFailure, smNoneUnreachable);

        Assert.NotNull(unavailable);
        Assert.Equal("localhost:27017", unavailable.Endpoint);
        Assert.Same(connectionFailure, unavailable.InnerException);
    }

    [Fact]
    public void AsUnavailableDoesNotReportARejectedLoginAsUnreachable()
    {
        var rejected = new MongoAuthenticationException(smConnectionId, "Unable to authenticate using sasl protocol mechanism SCRAM-SHA-256.");

        Assert.Null(DatabaseFailure.AsUnavailable(rejected, smLocalhostUnreachable));
    }

    [Fact]
    public void AsUnavailableTreatsATimeoutAsUnreachableOnlyWhileADatabaseIsKnownUnreachable()
    {
        var timeout = new TimeoutException("A timeout occurred after 30000ms selecting a server.");

        var whileDown = DatabaseFailure.AsUnavailable(timeout, smLocalhostUnreachable);

        Assert.NotNull(whileDown);
        Assert.Equal("localhost:27017", whileDown.Endpoint);
        Assert.Same(timeout, whileDown.InnerException);
        Assert.Null(DatabaseFailure.AsUnavailable(timeout, smNoneUnreachable));
    }

    [Fact]
    public void AsUnavailableJoinsEveryUnreachableEndpointForATimeout()
    {
        var unavailable = DatabaseFailure.AsUnavailable(new TimeoutException("timed out"), ["db1:27017", "db2:27017"]);

        Assert.NotNull(unavailable);
        Assert.Equal("db1:27017, db2:27017", unavailable.Endpoint);
    }

    [Fact]
    public void AsUnavailableLooksInsideAnAggregateFromASynchronousWait()
    {
        var unavailable = new DatabaseUnavailableException("localhost:27017", innerException: null);

        Assert.Same(unavailable, DatabaseFailure.AsUnavailable(new AggregateException(unavailable), smNoneUnreachable));
    }

    [Fact]
    public void AsUnavailableIgnoresUnrelatedFailuresAndCancellation()
    {
        Assert.Null(DatabaseFailure.AsUnavailable(new InvalidOperationException("unexpected"), smLocalhostUnreachable));
        Assert.Null(DatabaseFailure.AsUnavailable(new TaskCanceledException("cancelled", new TimeoutException("http")),
                                                  smLocalhostUnreachable
                                                 ));
    }

    private static readonly ConnectionId smConnectionId =
        new ConnectionId(new ServerId(new ClusterId(value: 1), new DnsEndPoint("localhost", port: 27017)));

    private static readonly string[] smNoneUnreachable = [];
    private static readonly string[] smLocalhostUnreachable = ["localhost:27017"];
}
