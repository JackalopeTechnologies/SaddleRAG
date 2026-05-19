// JobCancellationRegistryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Runtime.CompilerServices;
using SaddleRAG.Ingestion;

#endregion

namespace SaddleRAG.Tests.Ingestion;

public sealed class JobCancellationRegistryTests
{
    [Fact]
    public async Task TryCancelAsyncReturnsFalseForUnknownId()
    {
        var registry = new JobCancellationRegistry();
        var cancelled = await registry.TryCancelAsync(TestJobId);
        Assert.False(cancelled);
    }

    [Fact]
    public async Task RegisterThenTryCancelAsyncSignalsTheCts()
    {
        var registry = new JobCancellationRegistry();
        using var cts = new CancellationTokenSource();
        registry.Register(TestJobId, cts);

        var cancelled = await registry.TryCancelAsync(TestJobId);

        Assert.True(cancelled);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task UnregisterThenTryCancelAsyncReturnsFalseAndDoesNotSignal()
    {
        var registry = new JobCancellationRegistry();
        using var cts = new CancellationTokenSource();
        registry.Register(TestJobId, cts);

        registry.Unregister(TestJobId);

        var cancelled = await registry.TryCancelAsync(TestJobId);
        Assert.False(cancelled);
        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task ReRegisterReplacesPriorEntry()
    {
        var registry = new JobCancellationRegistry();
        using var firstCts = new CancellationTokenSource();
        using var secondCts = new CancellationTokenSource();

        registry.Register(TestJobId, firstCts);
        registry.Register(TestJobId, secondCts);

        await registry.TryCancelAsync(TestJobId);

        Assert.False(firstCts.IsCancellationRequested);
        Assert.True(secondCts.IsCancellationRequested);
    }

    [Fact]
    public void UnregisterUnknownIdIsNoOp()
    {
        var registry = new JobCancellationRegistry();
        var exception = Record.Exception(() => registry.Unregister(TestJobId));
        Assert.Null(exception);
    }

    [Fact]
    public void RegisterWithEmptyIdThrows()
    {
        var registry = new JobCancellationRegistry();
        using var cts = new CancellationTokenSource();
        Assert.Throws<ArgumentException>(() => registry.Register(string.Empty, cts));
    }

    [Fact]
    public void RegisterWithNullCtsThrows()
    {
        var registry = new JobCancellationRegistry();
        Assert.Throws<ArgumentNullException>(() => registry.Register(TestJobId, NullRef<CancellationTokenSource>()));
    }

    [Fact]
    public async Task ManyConcurrentRegisterUnregisterStaySafe()
    {
        var registry = new JobCancellationRegistry();
        var jobIds = Enumerable.Range(start: 0, count: ConcurrencyJobCount)
                               .Select(i => $"{TestJobId}-{i}")
                               .ToList();
        var ctsList = jobIds.Select(_ => new CancellationTokenSource()).ToList();

        var tasks = jobIds.Select((id, i) => Task.Run(() =>
        {
            registry.Register(id, ctsList[i]);
            registry.Unregister(id);
        })).ToArray();

        await Task.WhenAll(tasks);

        foreach(var id in jobIds)
        {
            var cancelled = await registry.TryCancelAsync(id);
            Assert.False(cancelled);
        }

        foreach(var cts in ctsList)
            cts.Dispose();
    }

    private static T NullRef<T>() where T : class
    {
        T? nullable = null;
        return Unsafe.As<T?, T>(ref nullable);
    }

    private const string TestJobId = "test-job-001";
    private const int ConcurrencyJobCount = 50;
}
