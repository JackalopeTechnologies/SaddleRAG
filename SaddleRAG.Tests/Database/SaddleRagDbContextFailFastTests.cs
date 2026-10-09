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
///     server is needed. The 4 s server-selection timeout is also the fail-fast
///     grace window, long enough to tell "failed fast" from "waited the timeout".
/// </summary>
public sealed class SaddleRagDbContextFailFastTests
{
    [Fact]
    public async Task ToolCallFailsFastOnceTheDatabaseHasBeenUnreachableForAFullSelectionTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = UnreachableDatabase.CreateSettings(out string endpoint, SelectionTimeoutMilliseconds);
        var context = new SaddleRagDbContext(Options.Create(settings));

        await Assert.ThrowsAnyAsync<Exception>(() => CountLibrariesAsync(context, ct));
        await UnreachableDatabase.WaitUntilUnreachableForAsync(context,
                                                               TimeSpan.FromMilliseconds(SelectionTimeoutMilliseconds),
                                                               ct
                                                              );

        var stopwatch = Stopwatch.StartNew();
        DatabaseUnavailableException ex;
        using(DatabaseFailFastScope.Begin())
            ex = await Assert.ThrowsAsync<DatabaseUnavailableException>(() => CountLibrariesAsync(context, ct));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(seconds: 2),
                    $"Expected one quick re-check, then failure; took {stopwatch.Elapsed.TotalMilliseconds:F0} ms"
                   );
        Assert.Equal(endpoint, ex.Endpoint);
    }

    private static Task<long> CountLibrariesAsync(SaddleRagDbContext context, CancellationToken ct) =>
        context.Libraries.CountDocumentsAsync(FilterDefinition<LibraryRecord>.Empty, cancellationToken: ct);

    private const int SelectionTimeoutMilliseconds = 4000;
}
