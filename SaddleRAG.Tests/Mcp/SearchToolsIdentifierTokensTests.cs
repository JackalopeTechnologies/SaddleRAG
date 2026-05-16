// SearchToolsIdentifierTokensTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Mcp.Tools;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class SearchToolsIdentifierTokensTests
{
    [Fact]
    public void ExtractIdentifierTokensReturnsPascalCaseTokenForSingleIdentifierQuery()
    {
        var tokens = SearchTools.ExtractIdentifierTokens("MovePVT");
        Assert.Contains("MovePVT", tokens);
    }

    [Fact]
    public void ExtractIdentifierTokensReturnsDottedIdentifier()
    {
        var tokens = SearchTools.ExtractIdentifierTokens("AxisFault.Disabled");
        Assert.Contains("AxisFault.Disabled", tokens);
    }

    [Fact]
    public void ExtractIdentifierTokensExtractsIdentifierFromProseQuery()
    {
        var tokens = SearchTools.ExtractIdentifierTokens("how do I call MovePvt with axes");
        Assert.Contains("MovePvt", tokens);
    }

    [Fact]
    public void ExtractIdentifierTokensDedupes()
    {
        var tokens = SearchTools.ExtractIdentifierTokens("MovePvt MovePvt MovePvt");
        Assert.Single(tokens, "MovePvt");
    }

    [Fact]
    public void ExtractIdentifierTokensSkipsShortFragments()
    {
        var tokens = SearchTools.ExtractIdentifierTokens("a b MovePvt c");
        Assert.Contains("MovePvt", tokens);
        Assert.DoesNotContain("a", tokens);
        Assert.DoesNotContain("b", tokens);
        Assert.DoesNotContain("c", tokens);
    }

    [Fact]
    public void ExtractIdentifierTokensReturnsNoCompoundTokensForPureProse()
    {
        var tokens = SearchTools.ExtractIdentifierTokens("how do I configure homing");
        Assert.DoesNotContain(tokens,
                              t => t.Contains('.', StringComparison.Ordinal) ||
                                   t.Contains("::", StringComparison.Ordinal)
                             );
    }

    [Fact]
    public void ExtractIdentifierTokensPreservesQueryCaseSoQualifiedNameLookupCanCompareCaseInsensitively()
    {
        var tokens = SearchTools.ExtractIdentifierTokens("MovePVT");
        Assert.Equal("MovePVT", tokens.Single(t => t.Equals("MovePVT", StringComparison.Ordinal)));
    }
}
