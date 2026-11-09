// SaddleRagDbContextFactoryReachabilityTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

public sealed class SaddleRagDbContextFactoryReachabilityTests
{
    [Fact]
    public void GetUnreachableEndpointsIsEmptyBeforeAnyContextExists()
    {
        var factory = new SaddleRagDbContextFactory(Options.Create(UnreachableDatabase.CreateSettings(out string _)));

        Assert.Empty(factory.GetUnreachableEndpoints());
    }

    [Fact]
    public async Task GetUnreachableEndpointsListsADatabaseWhoseHeartbeatsFail()
    {
        (var factory, string endpoint) =
            await UnreachableDatabase.CreateFactoryWithUnreachableDefaultAsync(TestContext.Current.CancellationToken);

        Assert.Equal([endpoint], factory.GetUnreachableEndpoints());
    }
}
