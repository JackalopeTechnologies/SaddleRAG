// JsonlIoTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SaddleRAG.Packaging.Internal;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class JsonlIoTests
{
    private sealed record Sample(int Id, string Name);

    [Fact]
    public async Task RoundTripsAcrossBufferBoundaries()
    {
        var ct = TestContext.Current.CancellationToken;
        var items = Enumerable.Range(0, 1000)
                              .Select(i => new Sample(i, new string('x', 200)))
                              .ToList();

        using var stream = new MemoryStream();
        await using (var writer = new JsonlWriter<Sample>(stream, leaveOpen: true))
        {
            foreach (var item in items)
                await writer.WriteAsync(item, ct);
        }

        stream.Position = 0;
        var reader = new JsonlReader<Sample>(stream);
        var roundTripped = new List<Sample>();
        await foreach (var item in reader.ReadAllAsync(ct))
            roundTripped.Add(item);

        Assert.Equal(items.Count, roundTripped.Count);
        Assert.Equal(items, roundTripped);
    }

    [Fact]
    public async Task EmptyStreamYieldsNoItems()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        var reader = new JsonlReader<Sample>(stream);

        var any = false;
        await foreach (var _ in reader.ReadAllAsync(ct))
            any = true;

        Assert.False(any);
    }

    [Fact]
    public async Task MalformedLineThrowsWithLineNumber()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"Id\":1,\"Name\":\"ok\"}\n{garbage}\n");
        using var stream = new MemoryStream(bytes);
        var reader = new JsonlReader<Sample>(stream);

        var ex = await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await foreach (var _ in reader.ReadAllAsync(ct))
            {
            }
        });

        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public async Task BlankLinesAreSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "{\"Id\":1,\"Name\":\"a\"}\n\n{\"Id\":2,\"Name\":\"b\"}\n");
        using var stream = new MemoryStream(bytes);
        var reader = new JsonlReader<Sample>(stream);

        var items = new List<Sample>();
        await foreach (var item in reader.ReadAllAsync(ct))
            items.Add(item);

        Assert.Equal(2, items.Count);
        Assert.Equal("a", items[0].Name);
        Assert.Equal("b", items[1].Name);
    }

    [Fact]
    public async Task MultiLineJsonOnALineIsRejected()
    {
        // A record split across two lines is invalid JSONL.
        // Line 1 is incomplete JSON, line 2 is the orphan tail.
        // The reader must throw on line 1 (where parsing first fails),
        // not silently splice them together.
        var ct = TestContext.Current.CancellationToken;
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            "{\"Id\":1,\"Name\":\n\"x\"}\n");
        using var stream = new MemoryStream(bytes);
        var reader = new JsonlReader<Sample>(stream);

        var ex = await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await foreach (var _ in reader.ReadAllAsync(ct))
            {
            }
        });

        Assert.Contains("line 1", ex.Message);
    }

    [Fact]
    public async Task CancellationIsHonoredMidIteration()
    {
        var ct = TestContext.Current.CancellationToken;
        var items = Enumerable.Range(0, 100)
                              .Select(i => new Sample(i, "x"))
                              .ToList();

        using var stream = new MemoryStream();
        await using (var writer = new JsonlWriter<Sample>(stream, leaveOpen: true))
        {
            foreach (var item in items)
                await writer.WriteAsync(item, ct);
        }
        stream.Position = 0;

        using var cts = new CancellationTokenSource();
        var reader = new JsonlReader<Sample>(stream);

        var consumed = 0;
        var caught = false;
        try
        {
            await foreach (var _ in reader.ReadAllAsync(cts.Token))
            {
                consumed++;
                if (consumed == 1)
                    cts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            caught = true;
        }

        Assert.True(caught, "Cancellation should have been observed");
        Assert.Equal(1, consumed);
    }
}
