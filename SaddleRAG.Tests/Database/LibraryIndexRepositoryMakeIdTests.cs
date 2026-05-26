// LibraryIndexRepositoryMakeIdTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

public sealed class LibraryIndexRepositoryMakeIdTests
{
    [Fact]
    public void ComposesLibraryAndVersionWithSlashSeparator()
    {
        var id = LibraryIndexRepository.MakeId("aerotech-aeroscript", "current");
        Assert.Equal("aerotech-aeroscript/current", id);
    }

    [Fact]
    public void PreservesCaseAndPunctuationInLibraryId()
    {
        var id = LibraryIndexRepository.MakeId("Mongo.Driver", "3.7.1");
        Assert.Equal("Mongo.Driver/3.7.1", id);
    }

    [Fact]
    public void ThrowsWhenLibraryIdEmpty()
    {
        Assert.Throws<ArgumentException>(() => LibraryIndexRepository.MakeId(string.Empty, "1.0"));
    }

    [Fact]
    public void ThrowsWhenVersionEmpty()
    {
        Assert.Throws<ArgumentException>(() => LibraryIndexRepository.MakeId("foo", string.Empty));
    }
}
