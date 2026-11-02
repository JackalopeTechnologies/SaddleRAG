// LocalEndpointTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.RegularExpressions;
using SaddleRAG.Core.Hosting;
using SaddleRAG.Tests.Documents.Docling;

#endregion

namespace SaddleRAG.Tests.Core;

public sealed class LocalEndpointTests
{
    [Theory]
    [InlineData("http://localhost:5001", true)]
    [InlineData("http://LOCALHOST:5001", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:5001", true)]
    [InlineData("http://docling.lan:5001", false)]
    [InlineData("http://192.168.1.20:5001", false)]
    [InlineData("http://127.0.0.2:5001", false)]
    [InlineData("localhost:5001", false)]
    [InlineData("not a uri", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLoopbackUri(string? endpoint, bool expected) =>
        Assert.Equal(expected, LocalEndpoint.IsLoopbackUri(endpoint));

    [Theory]
    [InlineData("mongodb://localhost:27017", true)]
    [InlineData("mongodb://localhost", true)]
    [InlineData("MONGODB://LocalHost:27017", true)]
    [InlineData("mongodb://127.0.0.1:27017", true)]
    [InlineData("mongodb://[::1]:27017", true)]
    [InlineData("mongodb://user:p%40ss@127.0.0.1:27017/SaddleRAG?authSource=admin", true)]
    [InlineData("mongodb://localhost:27017,127.0.0.1:27018/?replicaSet=rs0", true)]
    [InlineData("mongodb://localhost:27017,db2.lan:27017/?replicaSet=rs0", false)]
    [InlineData("mongodb://db.lan:27017", false)]
    [InlineData("mongodb://user:localhost@db.lan:27017", false)]
    [InlineData("mongodb://db.lan/localhost", false)]
    [InlineData("mongodb://localhost:notaport", false)]
    [InlineData("mongodb://localhost:0", false)]
    [InlineData("mongodb://localhost:70000", false)]
    [InlineData("mongodb://", false)]
    [InlineData("mongodb:///SaddleRAG", false)]
    [InlineData("mongodb+srv://cluster0.example.net", false)]
    [InlineData("mongodb+srv://localhost", false)]
    [InlineData("http://localhost:27017", false)]
    [InlineData("localhost:27017", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLoopbackMongoConnectionString(string? connectionString, bool expected) =>
        Assert.Equal(expected, LocalEndpoint.IsLoopbackMongoConnectionString(connectionString));

    [Theory]
    [InlineData("mongodb://localhost", 27017)]
    [InlineData("mongodb://localhost:27017", 27017)]
    [InlineData("mongodb://localhost:27018", 27018)]
    [InlineData("mongodb://user:p%40ss@127.0.0.1:28000/SaddleRAG?authSource=admin", 28000)]
    [InlineData("mongodb://[::1]:27019", 27019)]
    [InlineData("mongodb://[::1]", 27017)]
    [InlineData("mongodb://localhost:27020,db2.lan:27017/?replicaSet=rs0", 27020)]
    [InlineData("mongodb://db.lan:27021", 27021)]
    public void MongoPortReadsFirstHost(string connectionString, int expected) =>
        Assert.Equal(expected, LocalEndpoint.MongoPort(connectionString));

    [Fact]
    public void MongoPortDefaultsTo27017() =>
        Assert.Equal(27017, LocalEndpoint.MongoPort("mongodb://localhost"));

    [Theory]
    [InlineData("mongodb://localhost:notaport")]
    [InlineData("mongodb://localhost:0")]
    [InlineData("mongodb://localhost:70000")]
    [InlineData("mongodb://")]
    [InlineData("mongodb+srv://cluster0.example.net")]
    [InlineData("http://localhost:27017")]
    [InlineData("")]
    [InlineData(null)]
    public void MongoPortIsNullWhenNotParseable(string? connectionString) =>
        Assert.Null(LocalEndpoint.MongoPort(connectionString));

    [Fact]
    public void SeparatorsAndBracketIndexesAreNamedConstants()
    {
        // Jackalope no-magic-literal rule: every separator character is a named constant, and the
        // bracketed-host split uses no bare index.
        string source = File.ReadAllText(Path.Combine(DoclingTestSupport.RepositoryRoot(),
                                                      "SaddleRAG.Core",
                                                      "Hosting",
                                                      "LocalEndpoint.cs"));
        string[] code = source.Split('\n')
                              .Select(line => line.Trim())
                              .Where(line => !line.StartsWith("//", StringComparison.Ordinal) &&
                                             !line.StartsWith("private const", StringComparison.Ordinal))
                              .ToArray();

        Assert.DoesNotContain(code, line => Regex.IsMatch(line, @"'(\\.|[^'])'"));

        int start = source.IndexOf("private static HostText? SplitBracketedHost", StringComparison.Ordinal);
        int end = source.IndexOf("private static HostText? SplitPlainHost", StringComparison.Ordinal);
        string bracketed = string.Join('\n',
                                       source[start..end].Split('\n')
                                                         .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotMatch(@"(?<![\w.])\d+(?![\w])", bracketed);
    }
}
