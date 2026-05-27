// JsonlIoTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.IO;
using System.Linq;
using System.Text.Json;
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
}
