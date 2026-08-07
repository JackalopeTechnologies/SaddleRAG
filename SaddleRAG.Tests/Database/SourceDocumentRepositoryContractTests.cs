// SourceDocumentRepositoryContractTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Database.Repositories;

namespace SaddleRAG.Tests.Database;

public sealed class SourceDocumentRepositoryContractTests
{
    [Fact]
    public void RevisionIdIsDeterministicAndVersionScoped()
    {
        var first = SourceDocumentRepository.MakeRevisionId("library", "2026-08-04", "document");
        var second = SourceDocumentRepository.MakeRevisionId("library", "2026-08-04", "document");
        var nextVersion = SourceDocumentRepository.MakeRevisionId("library", "2026-08-05", "document");

        Assert.Equal(first, second);
        Assert.NotEqual(first, nextVersion);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("short", false)]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    public void Sha256ValidationRequiresCanonicalLowercaseHex(string value, bool expected)
    {
        Assert.Equal(expected, SourceDocumentRepository.IsCanonicalSha256(value));
    }

    [Fact]
    public void ArtifactFilenameIsHashAddressed()
    {
        const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        Assert.Equal($"sha256/{Hash}", SourceDocumentRepository.MakeArtifactFilename(Hash));
    }
}
