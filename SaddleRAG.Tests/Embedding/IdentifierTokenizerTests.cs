// IdentifierTokenizerTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class IdentifierTokenizerTests
{
    [Fact]
    public void ExtractDistinctReturnsPascalCaseTokenForSingleIdentifierQuery()
    {
        var tokens = IdentifierTokenizer.ExtractDistinct("MovePVT", MinLength);
        Assert.Contains("MovePVT", tokens);
    }

    [Fact]
    public void ExtractDistinctReturnsDottedIdentifier()
    {
        var tokens = IdentifierTokenizer.ExtractDistinct("AxisFault.Disabled", MinLength);
        Assert.Contains("AxisFault.Disabled", tokens);
    }

    [Fact]
    public void ExtractDistinctExtractsIdentifierFromProseQuery()
    {
        var tokens = IdentifierTokenizer.ExtractDistinct("how do I call MovePvt with axes", MinLength);
        Assert.Contains("MovePvt", tokens);
    }

    [Fact]
    public void ExtractDistinctDedupes()
    {
        var tokens = IdentifierTokenizer.ExtractDistinct("MovePvt MovePvt MovePvt", MinLength);
        Assert.Single(tokens, "MovePvt");
    }

    [Fact]
    public void ExtractDistinctSkipsShortFragments()
    {
        var tokens = IdentifierTokenizer.ExtractDistinct("a b MovePvt c", MinLength);
        Assert.Contains("MovePvt", tokens);
        Assert.DoesNotContain("a", tokens);
        Assert.DoesNotContain("b", tokens);
        Assert.DoesNotContain("c", tokens);
    }

    [Fact]
    public void ExtractDistinctReturnsNoCompoundTokensForPureProse()
    {
        var tokens = IdentifierTokenizer.ExtractDistinct("how do I configure homing", MinLength);
        Assert.DoesNotContain(tokens,
                              t => t.Contains('.', StringComparison.Ordinal) ||
                                   t.Contains("::", StringComparison.Ordinal)
                             );
    }

    [Fact]
    public void EmitRawAndLowercaseProducesBothFormsForMixedCaseIdentifier()
    {
        var tokens = IdentifierTokenizer.EmitRawAndLowercase("AxisFault.Disabled", MinLength).ToList();
        Assert.Contains("AxisFault.Disabled", tokens);
        Assert.Contains("axisfault.disabled", tokens);
    }

    [Fact]
    public void EmitRawAndLowercaseOmitsRedundantLowerWhenAlreadyLowercase()
    {
        var tokens = IdentifierTokenizer.EmitRawAndLowercase("movepvt", MinLength).ToList();
        Assert.Single(tokens, "movepvt");
    }

    private const int MinLength = 2;
}
