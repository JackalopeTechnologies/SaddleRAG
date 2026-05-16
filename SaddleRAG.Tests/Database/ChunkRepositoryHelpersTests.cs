// ChunkRepositoryHelpersTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Database.Repositories;

#endregion

namespace SaddleRAG.Tests.Database;

/// <summary>
///     Pure-helper coverage for <see cref="ChunkRepository" />. The two
///     methods exercised here back the <c>list_classes</c> / <c>list_symbols</c>
///     MCP-tool surfaces — a regression in either (e.g. swapping which
///     SymbolKind is filtered, dropping the legacy fallback) ships
///     visibly wrong API-reference output without touching the Mongo
///     query layer the repository's other methods need to integration-
///     test against.
/// </summary>
public sealed class ChunkRepositoryHelpersTests
{
    [Fact]
    public void ProjectTypeNamesPicksOnlyTypeKindFromV2Chunks()
    {
        var chunks = new[]
                         {
                             MakeChunk(parserVersion: 2,
                                       symbols:
                                       [
                                           new Symbol { Name = "MyClass", Kind = SymbolKind.Type, Container = "" },
                                           new Symbol { Name = "myMethod", Kind = SymbolKind.Function, Container = "" }
                                       ]
                                      )
                         };

        var names = ChunkRepository.ProjectTypeNames(chunks).ToList();

        Assert.Single(names, "MyClass");
    }

    [Fact]
    public void ProjectTypeNamesFallsBackToQualifiedNameForLegacyV1Chunks()
    {
        var chunks = new[]
                         {
                             MakeChunk(parserVersion: 1,
                                       symbols: [],
                                       qualifiedName: "Legacy.Type"
                                      )
                         };

        var names = ChunkRepository.ProjectTypeNames(chunks).ToList();

        Assert.Single(names, "Legacy.Type");
    }

    [Fact]
    public void ProjectTypeNamesSkipsLegacyChunksWithEmptyQualifiedName()
    {
        var chunks = new[]
                         {
                             MakeChunk(parserVersion: 1,
                                       symbols: [],
                                       qualifiedName: string.Empty
                                      )
                         };

        Assert.Empty(ChunkRepository.ProjectTypeNames(chunks));
    }

    [Fact]
    public void ProjectTypeNamesSkipsV2ChunksWithEmptySymbolList()
    {
        var chunks = new[] { MakeChunk(parserVersion: 2, symbols: []) };
        Assert.Empty(ChunkRepository.ProjectTypeNames(chunks));
    }

    [Fact]
    public void ProjectTypeNamesYieldsAcrossMultipleChunksAndKinds()
    {
        var chunks = new[]
                         {
                             MakeChunk(parserVersion: 2,
                                       symbols:
                                       [
                                           new Symbol { Name = "ClassA", Kind = SymbolKind.Type, Container = "" }
                                       ]
                                      ),
                             MakeChunk(parserVersion: 2,
                                       symbols:
                                       [
                                           new Symbol { Name = "ClassB", Kind = SymbolKind.Type, Container = "" },
                                           new Symbol { Name = "fooFn", Kind = SymbolKind.Function, Container = "" }
                                       ]
                                      ),
                             MakeChunk(parserVersion: 1, symbols: [], qualifiedName: "LegacyC")
                         };

        var names = ChunkRepository.ProjectTypeNames(chunks).ToList();

        Assert.Equal(expected: 3, names.Count);
        Assert.Contains("ClassA", names);
        Assert.Contains("ClassB", names);
        Assert.Contains("LegacyC", names);
        Assert.DoesNotContain("fooFn", names);
    }

    [Fact]
    public void ApplyFilterWithNullReturnsAllNames()
    {
        var names = new[] { "Alpha", "Bravo", "Charlie" };
        Assert.Equal(names, ChunkRepository.ApplyFilter(names, filter: null));
    }

    [Fact]
    public void ApplyFilterWithEmptyStringReturnsAllNames()
    {
        var names = new[] { "Alpha", "Bravo" };
        Assert.Equal(names, ChunkRepository.ApplyFilter(names, filter: string.Empty));
    }

    [Fact]
    public void ApplyFilterWithWhitespaceReturnsAllNames()
    {
        var names = new[] { "Alpha", "Bravo" };
        Assert.Equal(names, ChunkRepository.ApplyFilter(names, filter: "   "));
    }

    [Fact]
    public void ApplyFilterMatchesCaseInsensitiveSubstring()
    {
        var names = new[] { "AlphaBetaGamma", "OnlyBeta", "Other" };
        var filtered = ChunkRepository.ApplyFilter(names, filter: "BETA").ToList();
        Assert.Equal(expected: 2, filtered.Count);
        Assert.Contains("AlphaBetaGamma", filtered);
        Assert.Contains("OnlyBeta", filtered);
    }

    [Fact]
    public void ApplyFilterReturnsEmptyWhenNoMatch()
    {
        var names = new[] { "Alpha", "Bravo" };
        Assert.Empty(ChunkRepository.ApplyFilter(names, filter: "Xylophone"));
    }

    private static DocChunk MakeChunk(int parserVersion,
                                      IReadOnlyList<Symbol> symbols,
                                      string? qualifiedName = null) =>
        new DocChunk
            {
                Id = $"chunk-{Guid.NewGuid():N}",
                LibraryId = "lib",
                Version = "1.0",
                PageUrl = "https://example.com",
                PageTitle = "Page",
                Category = DocCategory.ApiReference,
                Content = "content",
                ParserVersion = parserVersion,
                Symbols = symbols,
                QualifiedName = qualifiedName
            };
}
