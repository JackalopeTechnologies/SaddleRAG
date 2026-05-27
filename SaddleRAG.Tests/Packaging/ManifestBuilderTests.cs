// ManifestBuilderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
        var builder = new ManifestBuilder();

        using (var a = builder.OpenBlob("a.bin"))
            await a.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        using (var b = builder.OpenBlob("b.bin"))
            await b.WriteAsync(new byte[] { 4, 5, 6, 7 }, TestContext.Current.CancellationToken);

        Assert.Equal(3, builder.GetBlob("a.bin").Bytes);
        Assert.Equal(4, builder.GetBlob("b.bin").Bytes);
        Assert.NotEqual(builder.GetBlob("a.bin").Sha256, builder.GetBlob("b.bin").Sha256);
    }
}
