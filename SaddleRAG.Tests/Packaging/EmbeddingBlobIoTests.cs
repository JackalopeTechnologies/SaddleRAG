// EmbeddingBlobIoTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SaddleRAG.Packaging.Internal;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class EmbeddingBlobIoTests
{
    [Fact]
    public async Task RoundTripsExactBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        const int dim = 768;
        var rng = new Random(42);
        var vectors = Enumerable.Range(0, 100)
                                .Select(_ => Enumerable.Range(0, dim)
                                                       .Select(_ => (float)(rng.NextDouble() * 2.0 - 1.0))
                                                       .ToArray())
                                .ToList();

        using var stream = new MemoryStream();
        await using (var writer = new EmbeddingBlobWriter(stream, dim, leaveOpen: true))
        {
            foreach (var v in vectors)
                await writer.WriteAsync(v, ct);
        }

        Assert.Equal(checked(vectors.Count * dim * sizeof(float)), stream.Length);

        stream.Position = 0;
        var reader = new EmbeddingBlobReader(stream, dim);
        var readBack = new List<float[]>();
        for (int i = 0; i < vectors.Count; i++)
            readBack.Add(await reader.ReadAsync(ct));

        Assert.Equal(vectors.Count, readBack.Count);
        for (int i = 0; i < vectors.Count; i++)
            Assert.Equal(vectors[i], readBack[i]);
    }

    [Fact]
    public async Task WriterRejectsWrongDimensionVector()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream();
        await using var writer = new EmbeddingBlobWriter(stream, dim: 768, leaveOpen: true);

        var bad = new float[512];

        await Assert.ThrowsAsync<ArgumentException>(async () => await writer.WriteAsync(bad, ct));
    }

    [Fact]
    public async Task ReaderThrowsOnUnexpectedEof()
    {
        var ct = TestContext.Current.CancellationToken;
        var truncated = new byte[10];
        using var stream = new MemoryStream(truncated);
        var reader = new EmbeddingBlobReader(stream, dim: 768);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync(ct));
    }

    [Fact]
    public async Task PreservesSpecialFloatValues()
    {
        var ct = TestContext.Current.CancellationToken;
        const int dim = 8;
        var special = new float[]
                          {
                              float.NaN,
                              float.PositiveInfinity,
                              float.NegativeInfinity,
                              -0.0f,
                              0.0f,
                              float.Epsilon,
                              float.MaxValue,
                              float.MinValue
                          };

        using var stream = new MemoryStream();
        await using (var writer = new EmbeddingBlobWriter(stream, dim, leaveOpen: true))
        {
            await writer.WriteAsync(special, ct);
        }

        stream.Position = 0;
        var reader = new EmbeddingBlobReader(stream, dim);
        var readBack = await reader.ReadAsync(ct);

        Assert.Equal(special.Length, readBack.Length);
        for (int i = 0; i < special.Length; i++)
        {
            var expectedBits = BitConverter.SingleToInt32Bits(special[i]);
            var actualBits = BitConverter.SingleToInt32Bits(readBack[i]);
            Assert.Equal(expectedBits, actualBits);
        }
    }
}
