// SaddleRagDbContextFailFastTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;
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
        var context = new SaddleRagDbContext(Options.Create(UnreachableDatabase.CreateSettings(out string endpoint)));

        await Assert.ThrowsAnyAsync<Exception>(() => CountLibrariesAsync(context, ct));
        await UnreachableDatabase.WaitUntilUnreachableForAsync(context, TimeSpan.FromSeconds(seconds: 2), ct);

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => CountLibrariesAsync(context, ct));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(seconds: 1),
                    $"Expected an immediate failure, took {stopwatch.Elapsed.TotalMilliseconds:F0} ms"
                   );
        Assert.Equal(endpoint, ex.Endpoint);
    }

    private static Task<long> CountLibrariesAsync(SaddleRagDbContext context, CancellationToken ct) =>
        context.Libraries.CountDocumentsAsync(FilterDefinition<LibraryRecord>.Empty, cancellationToken: ct);
}
