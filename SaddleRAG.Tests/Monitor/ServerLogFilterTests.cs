// ServerLogFilterTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class ServerLogFilterTests
{
    [Fact]
    public void WarningsPlusKeepsWarningAndAbove()
    {
        var filtered = ServerLogFilter.Apply(AllLevels(), ServerLogLevelFilter.WarningsPlus, text: null);

        Assert.Equal(expected: 3, filtered.Count);
        Assert.All(filtered, e => Assert.True(e.Level >= ServerLogLevel.Warning));
    }

    [Fact]
    public void ErrorsOnlyKeepsErrorAndFatal()
    {
        var filtered = ServerLogFilter.Apply(AllLevels(), ServerLogLevelFilter.ErrorsOnly, text: null);

        Assert.Equal(expected: 2, filtered.Count);
        Assert.All(filtered, e => Assert.True(e.Level >= ServerLogLevel.Error));
    }

    [Fact]
    public void TextFilterMatchesMessageCaseInsensitively()
    {
        var filtered = ServerLogFilter.Apply(AllLevels(), ServerLogLevelFilter.All, "ENTRY-INF");

        Assert.Equal("entry-Information", Assert.Single(filtered).Message);
    }

    [Fact]
    public void TextFilterMatchesDetailLines()
    {
        var entries = new[]
                          {
                              Entry(ServerLogLevel.Error, "boom", "OnnxRuntimeException: device gone")
                          };

        var filtered = ServerLogFilter.Apply(entries, ServerLogLevelFilter.All, "onnxruntime");

        Assert.Single(filtered);
    }

    [Fact]
    public void BlankTextMeansNoTextFiltering()
    {
        var filtered = ServerLogFilter.Apply(AllLevels(), ServerLogLevelFilter.All, "   ");

        Assert.Equal(AllLevels().Count, filtered.Count);
    }

    private static IReadOnlyList<ServerLogEntry> AllLevels()
    {
        return
        [
            Entry(ServerLogLevel.Verbose, "entry-Verbose"),
            Entry(ServerLogLevel.Debug, "entry-Debug"),
            Entry(ServerLogLevel.Information, "entry-Information"),
            Entry(ServerLogLevel.Warning, "entry-Warning"),
            Entry(ServerLogLevel.Error, "entry-Error"),
            Entry(ServerLogLevel.Fatal, "entry-Fatal")
        ];
    }

    private static ServerLogEntry Entry(ServerLogLevel level, string message, params string[] detail)
    {
        return new ServerLogEntry
                   {
                       Timestamp = DateTimeOffset.UtcNow,
                       Level = level,
                       Message = message,
                       DetailLines = detail
                   };
    }
}
