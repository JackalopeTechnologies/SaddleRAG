// SaddleRagDbContextFailFastTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SaddleRAG.Core.Models;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Drives a real MongoDB driver at a port nothing listens on, so no MongoDB
///     server is needed. The connection string shortens the server-selection
///     timeout to 2 s, which is also the fail-fast grace window.
/// </summary>
public sealed class SaddleRagDbContextFailFastTests
{
    [Fact]
    public async Task OperationFailsFastOnceTheDatabaseHasBeenUnreachableForAFullSelectionTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        int port = GetClosedLoopbackPort();
        var settings = new SaddleRagDbSettings
                           {
                               ConnectionString =
                                   $"mongodb://127.0.0.1:{port}/?serverSelectionTimeoutMS=2000&connectTimeoutMS=500",
                               DatabaseName = "saddlerag-failfast-test"
                           };
        var context = new SaddleRagDbContext(Options.Create(settings));

        await Assert.ThrowsAnyAsync<Exception>(() => CountLibrariesAsync(context, ct));
        await WaitUntilUnreachableForAsync(context, TimeSpan.FromSeconds(seconds: 2), ct);

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => CountLibrariesAsync(context, ct));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(seconds: 1),
                    $"Expected an immediate failure, took {stopwatch.Elapsed.TotalMilliseconds:F0} ms"
                   );
        Assert.Equal($"127.0.0.1:{port}", ex.Endpoint);
    }

    private static Task<long> CountLibrariesAsync(SaddleRagDbContext context, CancellationToken ct) =>
        context.Libraries.CountDocumentsAsync(FilterDefinition<LibraryRecord>.Empty, cancellationToken: ct);

    private static async Task WaitUntilUnreachableForAsync(SaddleRagDbContext context,
                                                           TimeSpan duration,
                                                           CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds: 20);
        while (!context.Reachability.HasBeenUnreachableFor(duration) && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds: 100), ct);

        Assert.True(context.Reachability.HasBeenUnreachableFor(duration),
                    "The driver never reported the closed port as unreachable."
                   );
    }

    private static int GetClosedLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
