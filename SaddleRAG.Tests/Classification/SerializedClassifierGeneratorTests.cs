// SerializedClassifierGeneratorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Runtime.CompilerServices;
using SaddleRAG.Ingestion.Classification;

#endregion

namespace SaddleRAG.Tests.Classification;

/// <summary>
///     Exercises <see cref="SerializedClassifierGenerator" />, the decorator
///     that guarantees at most one in-flight <c>GenerateAsync</c> against the
///     wrapped generator. onnxruntime-genai native <c>Generator</c> creation
///     over a shared <c>Model</c> is not thread-safe (issue #135: recurring
///     0xc0000005 in OgaCreateGenerator), so every composition root must wrap
///     the ONNX generator in this decorator.
/// </summary>
public sealed class SerializedClassifierGeneratorTests
{
    private const int ConcurrentCallCount = 16;
    private const int SecondCallGraceMilliseconds = 100;

    private sealed class ConcurrencyTrackingGenerator : IClassifierGenerator
    {
        private int mInFlight;
        private int mMaxInFlight;
        private int mEnteredCount;

        public string ModelId => "tracking-model";

        public int MaxInFlight => mMaxInFlight;

        public int EnteredCount => mEnteredCount;

        public TaskCompletionSource? HoldOpen { get; set; }

        public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref mEnteredCount);
            int inFlight = Interlocked.Increment(ref mInFlight);

            int observedMax = Volatile.Read(ref mMaxInFlight);
            while (inFlight > observedMax)
            {
                Interlocked.CompareExchange(ref mMaxInFlight, inFlight, observedMax);
                observedMax = Volatile.Read(ref mMaxInFlight);
            }

            if (HoldOpen != null)
                await HoldOpen.Task.WaitAsync(ct);
            else
                await Task.Yield();

            Interlocked.Decrement(ref mInFlight);
            return prompt;
        }
    }

    [Fact]
    public async Task ConcurrentCallsNeverOverlapOnInnerGenerator()
    {
        var inner = new ConcurrencyTrackingGenerator();
        var serialized = new SerializedClassifierGenerator(inner);

        var calls = Enumerable.Range(0, ConcurrentCallCount)
                              .Select(i => serialized.GenerateAsync($"prompt-{i}",
                                                                    TestContext.Current.CancellationToken))
                              .ToArray();
        string[] results = await Task.WhenAll(calls);

        Assert.Equal(1, inner.MaxInFlight);
        Assert.Equal(ConcurrentCallCount, inner.EnteredCount);
        Assert.Equal(ConcurrentCallCount, results.Distinct().Count());
    }

    [Fact]
    public async Task SecondCallWaitsUntilFirstCompletes()
    {
        var inner = new ConcurrencyTrackingGenerator
            {
                HoldOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            };
        var serialized = new SerializedClassifierGenerator(inner);

        Task<string> first = serialized.GenerateAsync("first", TestContext.Current.CancellationToken);
        Task<string> second = serialized.GenerateAsync("second", TestContext.Current.CancellationToken);

        await Task.Delay(SecondCallGraceMilliseconds, TestContext.Current.CancellationToken);
        Assert.Equal(1, inner.EnteredCount);

        inner.HoldOpen.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, inner.EnteredCount);
    }

    [Fact]
    public async Task ResultAndPromptPassThroughUnchanged()
    {
        var inner = new ConcurrencyTrackingGenerator();
        var serialized = new SerializedClassifierGenerator(inner);

        string result = await serialized.GenerateAsync("echo-me", TestContext.Current.CancellationToken);

        Assert.Equal("echo-me", result);
    }

    [Fact]
    public void ModelIdDelegatesToInner()
    {
        var inner = new ConcurrencyTrackingGenerator();
        var serialized = new SerializedClassifierGenerator(inner);

        Assert.Equal("tracking-model", serialized.ModelId);
    }

    [Fact]
    public void NullInnerThrows()
    {
        Assert.Throws<ArgumentNullException>(() => new SerializedClassifierGenerator(NullRef<IClassifierGenerator>()));
    }

    private static T NullRef<T>() where T : class
    {
        T? nullable = null;
        return Unsafe.As<T?, T>(ref nullable);
    }

    [Fact]
    public async Task EmptyPromptThrows()
    {
        var inner = new ConcurrencyTrackingGenerator();
        var serialized = new SerializedClassifierGenerator(inner);

        await Assert.ThrowsAsync<ArgumentException>(
            () => serialized.GenerateAsync(string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledWaiterDoesNotEnterInnerGenerator()
    {
        var inner = new ConcurrencyTrackingGenerator
            {
                HoldOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            };
        var serialized = new SerializedClassifierGenerator(inner);
        using var cts = new CancellationTokenSource();

        Task<string> first = serialized.GenerateAsync("first", TestContext.Current.CancellationToken);
        Task<string> second = serialized.GenerateAsync("second", cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        inner.HoldOpen.SetResult();
        await first;

        Assert.Equal(1, inner.EnteredCount);
    }
}
