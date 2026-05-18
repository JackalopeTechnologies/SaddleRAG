// OllamaBootstrapperWaitForReachableTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OllamaBootstrapperWaitForReachableTests
{
    [Fact]
    public async Task ReturnsTrueImmediatelyWhenProbeSucceedsOnFirstAttempt()
    {
        var attempts = 0;
        Task<bool> Probe(CancellationToken _)
        {
            attempts++;
            return Task.FromResult(true);
        }

        var result = await OllamaBootstrapper.WaitForReachableAsync(Probe,
                                                                    maxAttempts: 5,
                                                                    delayMs: 0,
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.True(result);
        Assert.Equal(expected: 1, attempts);
    }

    [Fact]
    public async Task ReturnsTrueWhenProbeEventuallySucceedsBeforeMaxAttempts()
    {
        var attempts = 0;
        Task<bool> Probe(CancellationToken _)
        {
            attempts++;
            return Task.FromResult(attempts >= 3);
        }

        var result = await OllamaBootstrapper.WaitForReachableAsync(Probe,
                                                                    maxAttempts: 5,
                                                                    delayMs: 0,
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.True(result);
        Assert.Equal(expected: 3, attempts);
    }

    [Fact]
    public async Task ReturnsFalseAfterExhaustingMaxAttempts()
    {
        var attempts = 0;
        Task<bool> Probe(CancellationToken _)
        {
            attempts++;
            return Task.FromResult(false);
        }

        var result = await OllamaBootstrapper.WaitForReachableAsync(Probe,
                                                                    maxAttempts: 4,
                                                                    delayMs: 0,
                                                                    TestContext.Current.CancellationToken
                                                                   );

        Assert.False(result);
        Assert.Equal(expected: 4, attempts);
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        Task<bool> Probe(CancellationToken token)
        {
            attempts++;
            return Task.FromResult(false);
        }

        cts.CancelAfter(millisecondsDelay: 50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OllamaBootstrapper.WaitForReachableAsync(Probe,
                                                            maxAttempts: 100,
                                                            delayMs: 25,
                                                            cts.Token
                                                           ));
        Assert.True(attempts < 100, $"expected to bail before exhausting attempts, got {attempts}");
    }

    [Fact]
    public async Task DoesNotSleepAfterFinalAttemptWhenProbeStillFails()
    {
        // Inject a counter in place of Task.Delay so the assertion is
        // deterministic and independent of wall-clock or scheduler jitter.
        var sleepCount = 0;
        Task DelayFactory(int ms, CancellationToken _)
        {
            sleepCount++;
            return Task.CompletedTask;
        }

        await OllamaBootstrapper.WaitForReachableAsync(_ => Task.FromResult(false),
                                                       maxAttempts: 2,
                                                       delayMs: 500,
                                                       TestContext.Current.CancellationToken,
                                                       DelayFactory
                                                      );

        // maxAttempts=2 → one inter-attempt sleep (between attempt 0 and 1),
        // zero sleeps after the final failed probe.
        Assert.Equal(expected: 1, sleepCount);
    }

    [Fact]
    public async Task ThrowsWhenMaxAttemptsIsZero()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => OllamaBootstrapper.WaitForReachableAsync(_ => Task.FromResult(true),
                                                            maxAttempts: 0,
                                                            delayMs: 0,
                                                            TestContext.Current.CancellationToken
                                                           ));
    }

    [Fact]
    public async Task ThrowsWhenDelayIsNegative()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => OllamaBootstrapper.WaitForReachableAsync(_ => Task.FromResult(true),
                                                            maxAttempts: 1,
                                                            delayMs: -1,
                                                            TestContext.Current.CancellationToken
                                                           ));
    }

}
