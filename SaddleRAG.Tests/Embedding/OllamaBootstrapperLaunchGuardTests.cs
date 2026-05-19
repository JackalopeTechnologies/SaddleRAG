// OllamaBootstrapperLaunchGuardTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Locks in the single-shot launch guard added after the duplicate-
///     ollama-process wedge: even if the bootstrap probe transiently
///     fails again, the bootstrapper will not spawn a second
///     <c>ollama serve</c>. Test seam:
///     <see cref="OllamaBootstrapper.pmProcessStarterOverride" /> + the
///     reset hook <see cref="OllamaBootstrapper.ResetLaunchGuardForTesting" />.
/// </summary>
public sealed class OllamaBootstrapperLaunchGuardTests : IDisposable
{
    public OllamaBootstrapperLaunchGuardTests()
    {
        OllamaBootstrapper.ResetLaunchGuardForTesting();
    }

    public void Dispose()
    {
        OllamaBootstrapper.pmProcessStarterOverride = null;
        OllamaBootstrapper.ResetLaunchGuardForTesting();
    }

    [Fact]
    public async Task SecondLaunchAttemptDoesNotSpawnAgain()
    {
        int spawnCount = 0;
        OllamaBootstrapper.pmProcessStarterOverride = _ =>
        {
            spawnCount++;
            return null;
        };

        var bootstrapper = MakeBootstrapper();
        // The internal launch loop polls IsReachableAsync for up to 30s
        // post-spawn. Test only cares about the guard, so cap wall-clock
        // with a short cancellation -- the loop sees OCE and exits.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(LaunchLoopCancelAfter);
        var ct = cts.Token;

        // First call: guard inactive, internal launch attempts the spawn.
        // The internal launch will throw TimeoutException because the
        // stubbed spawn returns null and IsReachableAsync never reports
        // a bound listener.
        await IgnoringExceptions(() => InvokeLaunchAsync(bootstrapper, ct));

        Assert.Equal(expected: 1, spawnCount);

        // Second call: guard fires, the override is never invoked.
        await InvokeLaunchAsync(bootstrapper, ct);

        Assert.Equal(expected: 1, spawnCount);
    }

    [Fact]
    public async Task ResetForTestingAllowsAnotherLaunchInSameProcess()
    {
        int spawnCount = 0;
        OllamaBootstrapper.pmProcessStarterOverride = _ =>
        {
            spawnCount++;
            return null;
        };

        var bootstrapper = MakeBootstrapper();
        // The internal launch loop polls IsReachableAsync for up to 30s
        // post-spawn. Test only cares about the guard, so cap wall-clock
        // with a short cancellation -- the loop sees OCE and exits.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(LaunchLoopCancelAfter);
        var ct = cts.Token;

        await IgnoringExceptions(() => InvokeLaunchAsync(bootstrapper, ct));
        Assert.Equal(expected: 1, spawnCount);

        // Without the reset, the second call is short-circuited. Verifying
        // the reset hook works in the same way production code would never
        // need to but tests rely on between cases.
        OllamaBootstrapper.ResetLaunchGuardForTesting();

        await IgnoringExceptions(() => InvokeLaunchAsync(bootstrapper, ct));
        Assert.Equal(expected: 2, spawnCount);
    }

    [Fact]
    public async Task GuardIsAtomicUnderConcurrentLaunchAttempts()
    {
        int spawnCount = 0;
        OllamaBootstrapper.pmProcessStarterOverride = _ =>
        {
            Interlocked.Increment(ref spawnCount);
            return null;
        };

        var bootstrapper = MakeBootstrapper();
        // The internal launch loop polls IsReachableAsync for up to 30s
        // post-spawn. Test only cares about the guard, so cap wall-clock
        // with a short cancellation -- the loop sees OCE and exits.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(LaunchLoopCancelAfter);
        var ct = cts.Token;

        // Fire several concurrent launches. Only one should actually
        // reach the spawn override even if the guard is racing.
        var tasks = Enumerable.Range(start: 0, count: ConcurrentLaunchAttempts)
                              .Select(_ => Task.Run(() => IgnoringExceptions(() => InvokeLaunchAsync(bootstrapper, ct)), ct))
                              .ToArray();

        await Task.WhenAll(tasks);

        Assert.Equal(expected: 1, spawnCount);
    }

    private static OllamaBootstrapper MakeBootstrapper()
    {
        var settings = Options.Create(new OllamaSettings
                                          {
                                              Endpoint = "http://127.0.0.1:39999",
                                              ClassificationModels =
                                              [
                                                  new OllamaModelEntry
                                                  {
                                                      Name = "phi4-mini:3.8b",
                                                      Description = "stub"
                                                  }
                                              ],
                                              ReconModels =
                                              [
                                                  new OllamaModelEntry
                                                  {
                                                      Name = "phi4-mini:3.8b",
                                                      Description = "stub"
                                                  }
                                              ]
                                          }
                                     );
        return new OllamaBootstrapper(settings, NullLogger<OllamaBootstrapper>.Instance);
    }

    private static Task InvokeLaunchAsync(OllamaBootstrapper bootstrapper, CancellationToken ct)
    {
        var method = typeof(OllamaBootstrapper).GetMethod("LaunchOllamaAsync",
                                                          BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var invocation = method.Invoke(bootstrapper, [ct]);
        Assert.NotNull(invocation);
        return (Task) invocation;
    }

    private static async Task IgnoringExceptions(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch
        {
            // Expected: stubbed spawn returns null, IsReachableAsync never
            // sees a bound listener, LaunchOllamaInternalAsync throws.
            // The launch guard's behavior is the subject of this test, not
            // the post-launch poll's failure mode.
        }
    }

    private const int ConcurrentLaunchAttempts = 8;
    private static readonly TimeSpan LaunchLoopCancelAfter = TimeSpan.FromMilliseconds(milliseconds: 100);
}
