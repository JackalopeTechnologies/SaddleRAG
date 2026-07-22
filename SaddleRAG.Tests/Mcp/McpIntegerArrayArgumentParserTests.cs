// McpIntegerArrayArgumentParserTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

#region Usings

using System.Text.Json;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class McpIntegerArrayArgumentParserTests
{
    [Fact]
    public void ParseReadsNativeJsonArray()
    {
        using JsonDocument document = JsonDocument.Parse("[405,503]");

        int[]? result = McpIntegerArrayArgumentParser.Parse(document.RootElement, parameterName: "statusCodes");

        Assert.NotNull(result);
        Assert.Equal([405, 503], result);
    }

    [Fact]
    public void ParseReadsJsonEncodedArrayString()
    {
        using JsonDocument document = JsonDocument.Parse("\"[405,503]\"");

        int[]? result = McpIntegerArrayArgumentParser.Parse(document.RootElement, parameterName: "statusCodes");

        Assert.NotNull(result);
        Assert.Equal([405, 503], result);
    }

    [Fact]
    public void ParseRejectsNonIntegerArray()
    {
        using JsonDocument document = JsonDocument.Parse("[\"405\"]");

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                                                                             McpIntegerArrayArgumentParser.Parse(document.RootElement,
                                                                                                                 parameterName: "statusCodes"
                                                                                                                )
                                                                        );

        Assert.Equal("statusCodes", exception.ParamName);
    }
}