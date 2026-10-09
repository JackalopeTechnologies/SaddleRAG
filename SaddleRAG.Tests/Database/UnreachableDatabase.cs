// UnreachableDatabase.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Points real MongoDB driver clients at a loopback port nothing listens on, so
///     tests can watch a genuinely unreachable database without a MongoDB server.
///     The 2 s server-selection timeout is also the fail-fast grace window.
/// </summary>
internal static class UnreachableDatabase
{
    public static SaddleRagDbSettings CreateSettings(out string endpoint,
                                                     int serverSelectionTimeoutMilliseconds = DefaultSelectionTimeoutMilliseconds)
    {
        int port = GetClosedLoopbackPort();
        endpoint = $"127.0.0.1:{port}";
        var settings = new SaddleRagDbSettings
                           {
                               ConnectionString =
                                   $"mongodb://{endpoint}/?serverSelectionTimeoutMS={serverSelectionTimeoutMilliseconds}&connectTimeoutMS=500",
                               DatabaseName = "saddlerag-unreachable-test"
                           };
        return settings;
    }

    public static async Task<(SaddleRagDbContextFactory Factory, string Endpoint)> CreateFactoryWithUnreachableDefaultAsync(
        CancellationToken ct)
    {
        var factory = new SaddleRagDbContextFactory(Options.Create(CreateSettings(out string endpoint)));
        var context = factory.GetDefault();
        await WaitUntilUnreachableForAsync(context, TimeSpan.Zero, ct);
        return (factory, endpoint);
    }

    public static async Task WaitUntilUnreachableForAsync(SaddleRagDbContext context,
                                                          TimeSpan duration,
                                                          CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + smWaitLimit;
        while (!context.Reachability.HasBeenUnreachableFor(duration) && DateTime.UtcNow < deadline)
            await Task.Delay(smPollInterval, ct);

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

    private const int DefaultSelectionTimeoutMilliseconds = 2000;
    private static readonly TimeSpan smWaitLimit = TimeSpan.FromSeconds(seconds: 20);
    private static readonly TimeSpan smPollInterval = TimeSpan.FromMilliseconds(milliseconds: 100);
}
