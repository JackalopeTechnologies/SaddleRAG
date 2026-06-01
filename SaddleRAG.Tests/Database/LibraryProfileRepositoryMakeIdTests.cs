// LibraryProfileRepositoryMakeIdTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Pins the LibraryProfile primary-key format. Callers building new
///     LibraryProfile records reuse <see cref="LibraryProfileRepository.MakeId" />
///     so a silent format change here would corrupt every freshly-recon'd
///     library version.
/// </summary>
public sealed class LibraryProfileRepositoryMakeIdTests
{
    [Theory]
    [InlineData("foo", "1.0", "foo/1.0")]
    [InlineData("bar", "v2", "bar/v2")]
    [InlineData("scoped.lib", "1.2.3", "scoped.lib/1.2.3")]
    public void MakeIdFormatsLibraryIdAndVersionAsSlashSeparatedPair(string libraryId,
                                                                     string version,
                                                                     string expected)
    {
        Assert.Equal(expected, LibraryProfileRepository.MakeId(libraryId, version));
    }

    [Fact]
    public void MakeIdThrowsArgumentExceptionOnEmptyLibraryId()
    {
        Assert.Throws<ArgumentException>(() => LibraryProfileRepository.MakeId(string.Empty, "v1"));
    }

    [Fact]
    public void MakeIdThrowsArgumentExceptionOnEmptyVersion()
    {
        Assert.Throws<ArgumentException>(() => LibraryProfileRepository.MakeId("lib", string.Empty));
    }
}
