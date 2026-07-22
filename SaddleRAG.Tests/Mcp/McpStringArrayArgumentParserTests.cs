// McpStringArrayArgumentParserTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License in the repo root.

#region Usings

using System.Text.Json;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class McpStringArrayArgumentParserTests
{
    [Fact]
    public void ParseReturnsNullForMissingArgument()
    {
        string[]? result = McpStringArrayArgumentParser.Parse(argument: null, parameterName: "patterns");

        Assert.Null(result);
    }

    [Fact]
    public void ParseReadsNativeJsonArray()
    {
        using JsonDocument document = JsonDocument.Parse("[\"one\",\"two\"]");

        string[]? result = McpStringArrayArgumentParser.Parse(document.RootElement, parameterName: "patterns");

        Assert.NotNull(result);
        Assert.Equal(["one", "two"], result);
    }

    [Fact]
    public void ParseReadsJsonEncodedArrayString()
    {
        using JsonDocument document = JsonDocument.Parse("\"[\\\"one\\\",\\\"two\\\"]\"");

        string[]? result = McpStringArrayArgumentParser.Parse(document.RootElement, parameterName: "patterns");

        Assert.NotNull(result);
        Assert.Equal(["one", "two"], result);
    }

    [Fact]
    public void ParseRejectsNonArrayValue()
    {
        using JsonDocument document = JsonDocument.Parse("42");

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                                                                             McpStringArrayArgumentParser.Parse(document.RootElement,
                                                                                                                parameterName: "patterns"
                                                                                                               )
                                                                        );

        Assert.Equal("patterns", exception.ParamName);
    }
}