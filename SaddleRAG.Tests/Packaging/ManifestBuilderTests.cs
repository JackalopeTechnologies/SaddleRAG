// ManifestBuilderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SaddleRAG.Packaging;
using SaddleRAG.Packaging.Internal;

#endregion

namespace SaddleRAG.Tests.Packaging;

public sealed class ManifestBuilderTests
{
    [Fact]
    public async Task ComputesCorrectSha256ForKnownInput()
    {
        var data = Encoding.UTF8.GetBytes("hello world");
        var expected = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        var builder = new ManifestBuilder();

        using (var sink = builder.OpenBlob("pages.jsonl"))
        {
            await sink.WriteAsync(data, TestContext.Current.CancellationToken);
        }

        var info = builder.GetBlob("pages.jsonl");
        Assert.Equal(expected, info.Sha256);
        Assert.Equal(data.LongLength, info.Bytes);
    }

    [Fact]
    public async Task TracksMultipleBlobsIndependently()
    {
        var aData = new byte[] { 1, 2, 3 };
        var bData = new byte[] { 4, 5, 6, 7 };
        var expectedASha = Convert.ToHexString(SHA256.HashData(aData)).ToLowerInvariant();
        var expectedBSha = Convert.ToHexString(SHA256.HashData(bData)).ToLowerInvariant();

        var builder = new ManifestBuilder();

        using (var a = builder.OpenBlob("a.bin"))
            await a.WriteAsync(aData, TestContext.Current.CancellationToken);
        using (var b = builder.OpenBlob("b.bin"))
            await b.WriteAsync(bData, TestContext.Current.CancellationToken);

        Assert.Equal(expectedASha, builder.GetBlob("a.bin").Sha256);
        Assert.Equal(aData.LongLength, builder.GetBlob("a.bin").Bytes);
        Assert.Equal(expectedBSha, builder.GetBlob("b.bin").Sha256);
        Assert.Equal(bData.LongLength, builder.GetBlob("b.bin").Bytes);
    }

    [Fact]
    public void GetBlobThrowsForUnknownPath()
    {
        var builder = new ManifestBuilder();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.GetBlob("never-opened.bin"));
        Assert.Contains("No blob recorded", ex.Message);
    }

    [Fact]
    public async Task GetBlobThrowsWhileSinkIsStillOpen()
    {
        var builder = new ManifestBuilder();

        var sink = builder.OpenBlob("in-progress.bin");
        try
        {
            await sink.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);

            var ex = Assert.Throws<InvalidOperationException>(() => builder.GetBlob("in-progress.bin"));
            Assert.Contains("still open", ex.Message);
        }
        finally
        {
            sink.Dispose();
        }

        // After dispose, GetBlob succeeds.
        Assert.Equal(3, builder.GetBlob("in-progress.bin").Bytes);
    }

    [Fact]
    public async Task SinkDisposeIsIdempotent()
    {
        var builder = new ManifestBuilder();

        var sink = builder.OpenBlob("once.bin");
        await sink.WriteAsync(new byte[] { 1, 2 }, TestContext.Current.CancellationToken);
        sink.Dispose();
        // Second dispose must not throw or double-record.
        sink.Dispose();

        var info = builder.GetBlob("once.bin");
        Assert.Equal(2, info.Bytes);
    }
}
