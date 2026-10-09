// DatabaseFailFastScopeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Database;

#endregion

namespace SaddleRAG.Tests.Database;

public sealed class DatabaseFailFastScopeTests
{
    [Fact]
    public void IsActiveOnlyBetweenBeginAndDispose()
    {
        Assert.False(DatabaseFailFastScope.IsActive);

        using(DatabaseFailFastScope.Begin())
            Assert.True(DatabaseFailFastScope.IsActive);

        Assert.False(DatabaseFailFastScope.IsActive);
    }

    [Fact]
    public async Task WorkStartedInsideAToolCallStopsFailingFastWhenTheCallEnds()
    {
        var release = new TaskCompletionSource();
        Task<bool> background;

        using(DatabaseFailFastScope.Begin())
        {
            background = Task.Run(async () =>
                                  {
                                      await release.Task;
                                      return DatabaseFailFastScope.IsActive;
                                  },
                                  TestContext.Current.CancellationToken
                                 );
        }

        release.SetResult();

        Assert.False(await background);
    }

    [Fact]
    public async Task AnotherFlowDoesNotSeeAToolCallsScope()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var toolCall = Task.Run(async () =>
                                {
                                    using(DatabaseFailFastScope.Begin())
                                    {
                                        entered.SetResult();
                                        await release.Task;
                                    }
                                },
                                TestContext.Current.CancellationToken
                               );

        await entered.Task;
        bool activeHere = DatabaseFailFastScope.IsActive;
        release.SetResult();
        await toolCall;

        Assert.False(activeHere);
    }
}
