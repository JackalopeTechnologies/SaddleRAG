// OllamaBootstrapperIsModelAvailableTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OllamaBootstrapperIsModelAvailableTests
{
    [Fact]
    public void MatchesExactName()
    {
        var available = MakeSet("phi4-mini:3.8b");
        Assert.True(OllamaBootstrapper.IsModelAvailable("phi4-mini:3.8b", available));
    }

    [Fact]
    public void MatchesCaseInsensitive()
    {
        var available = MakeSet("Phi4-Mini:3.8B");
        Assert.True(OllamaBootstrapper.IsModelAvailable("phi4-mini:3.8b", available));
    }

    [Fact]
    public void MatchesLatestSuffix()
    {
        var available = MakeSet("nomic-embed-text:latest");
        Assert.True(OllamaBootstrapper.IsModelAvailable("nomic-embed-text", available));
    }

    [Fact]
    public void MatchesPrefixForTagSpecificInstall()
    {
        // User requested `phi4`; available has `phi4:14b`. The bootstrapper
        // treats the tag-specific install as satisfying the bare-name request
        // so we don't pull a duplicate.
        var available = MakeSet("phi4:14b");
        Assert.True(OllamaBootstrapper.IsModelAvailable("phi4", available));
    }

    [Fact]
    public void ReturnsFalseWhenNothingMatches()
    {
        var available = MakeSet("nomic-embed", "llama3.2");
        Assert.False(OllamaBootstrapper.IsModelAvailable("phi4-mini", available));
    }

    [Fact]
    public void ReturnsFalseAgainstEmptySet()
    {
        var available = MakeSet();
        Assert.False(OllamaBootstrapper.IsModelAvailable("anything", available));
    }

    [Fact]
    public void ThrowsWhenModelIsEmpty()
    {
        Assert.Throws<ArgumentException>(() =>
            OllamaBootstrapper.IsModelAvailable(string.Empty, MakeSet()));
    }

    private static IReadOnlySet<string> MakeSet(params string[] names) =>
        names.ToHashSet(StringComparer.OrdinalIgnoreCase);
}
