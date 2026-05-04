// ListSymbolsToolTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;
using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class ListSymbolsToolTests
{
    [Fact]
    public async Task ListSymbolsClassKindReturnsClassesOnly()
    {
        (var factory, var libraryRepo, var chunkRepo) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "f",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = new List<string> { "1.0" }
                                }
                           );
        chunkRepo.GetSymbolsAsync("foo", "1.0", SymbolKind.Type, filter: null, Arg.Any<CancellationToken>())
                 .Returns(new[] { "ClassA", "ClassB" });

        var json = await LibraryTools.ListSymbols(factory,
                                                  "foo",
                                                  "class",
                                                  ct: TestContext.Current.CancellationToken
                                                 );

        Assert.Contains("\"ClassA\"", json);
        Assert.Contains("\"class\"", json);
    }

    [Fact]
    public async Task ListSymbolsNullKindReturnsAllKindsTagged()
    {
        (var factory, var libraryRepo, var chunkRepo) = MakeFactory();
        libraryRepo.GetLibraryAsync("foo", Arg.Any<CancellationToken>())
                   .Returns(new LibraryRecord
                                {
                                    Id = "foo",
                                    Name = "f",
                                    Hint = "h",
                                    CurrentVersion = "1.0",
                                    AllVersions = new List<string> { "1.0" }
                                }
                           );
        chunkRepo.GetAllSymbolsAsync("foo", "1.0", filter: null, Arg.Any<CancellationToken>())
                 .Returns(new[]
                              {
                                  new Symbol { Name = "ClassA", Kind = SymbolKind.Type },
                                  new Symbol { Name = "FuncB", Kind = SymbolKind.Function }
                              }
                         );

        var json = await LibraryTools.ListSymbols(factory,
                                                  "foo",
                                                  kind: null,
                                                  ct: TestContext.Current.CancellationToken
                                                 );

        Assert.Contains("\"ClassA\"", json);
        Assert.Contains("\"FuncB\"", json);
        Assert.Contains("\"class\"", json);
        Assert.Contains("\"function\"", json);
    }

    [Fact]
    public async Task ListSymbolsNotFoundReturnsErrorJson()
    {
        (var factory, var libraryRepo, var _) = MakeFactory();
        libraryRepo.GetLibraryAsync("missing", Arg.Any<CancellationToken>()).Returns((LibraryRecord?) null);

        var json = await LibraryTools.ListSymbols(factory,
                                                  "missing",
                                                  "class",
                                                  ct: TestContext.Current.CancellationToken
                                                 );

        Assert.Contains("not found", json, StringComparison.OrdinalIgnoreCase);
    }

    private static (RepositoryFactory factory, ILibraryRepository libraryRepo, IChunkRepository chunkRepo) MakeFactory()
    {
        var factory = Substitute.For<RepositoryFactory>(new object?[] { null });
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var chunkRepo = Substitute.For<IChunkRepository>();
        factory.GetLibraryRepository(Arg.Any<string?>()).Returns(libraryRepo);
        factory.GetChunkRepository(Arg.Any<string?>()).Returns(chunkRepo);
        return (factory, libraryRepo, chunkRepo);
    }
}
