// OnnxSettingsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OnnxSettingsTests
{
    [Fact]
    public void DefaultsMatchExpected()
    {
        var settings = new OnnxSettings();

        Assert.False(settings.Enabled);
        Assert.False(settings.EmbeddingEnabled);
        Assert.Equal(string.Empty, settings.ActiveEmbeddingModel);
        Assert.Equal(string.Empty, settings.ActiveRerankerModel);
        Assert.Equal(OnnxGraphOptimizationLevel.Basic, settings.GraphOptimizationLevel);
        Assert.Equal(expected: 0, settings.IntraOpNumThreads);
        Assert.Equal(OnnxSettings.DefaultRerankBatchSize, settings.RerankBatchSize);
        Assert.Empty(settings.EmbeddingModels);
        Assert.Empty(settings.RerankerModels);
        Assert.Equal(OnnxExecutionProvider.Cpu, settings.ExecutionProvider);
    }

    [Fact]
    public void ExecutionProviderConstantsCoverKnownValues()
    {
        Assert.Equal("Cpu", OnnxSettings.ExecutionProviderCpu);
        Assert.Equal("DirectMl", OnnxSettings.ExecutionProviderDirectMl);
        Assert.Equal("Cuda", OnnxSettings.ExecutionProviderCuda);
    }

    [Theory]
    [InlineData("Cpu", true)]
    [InlineData("cpu", true)]
    [InlineData("DirectMl", true)]
    [InlineData("directml", true)]
    [InlineData("Cuda", false)]
    [InlineData("cuda", false)]
    [InlineData("", false)]
    [InlineData("OpenVino", false)]
    public void IsKnownExecutionProviderRecognizesValidValuesOnly(string input, bool expected)
    {
        Assert.Equal(expected, OnnxSettings.IsKnownExecutionProvider(input));
    }

    [Fact]
    public void DefaultModelsDirIsUnderProgramData()
    {
        string dir = OnnxSettings.DefaultModelsDir;

        Assert.Contains("SaddleRAG", dir);
        Assert.Contains("onnx", dir, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("models", dir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetActiveEmbeddingModelReturnsFirstEntryWhenSelectorEmpty()
    {
        var settings = BuildSettingsWithEmbeddingRegistry();
        settings.ActiveEmbeddingModel = string.Empty;

        var active = settings.GetActiveEmbeddingModel();

        Assert.Equal("nomic", active.Name);
    }

    [Fact]
    public void GetActiveEmbeddingModelReturnsNamedEntry()
    {
        var settings = BuildSettingsWithEmbeddingRegistry();
        settings.ActiveEmbeddingModel = "minilm";

        var active = settings.GetActiveEmbeddingModel();

        Assert.Equal("minilm", active.Name);
    }

    [Fact]
    public void GetActiveEmbeddingModelThrowsForInvalidName()
    {
        var settings = BuildSettingsWithEmbeddingRegistry();
        settings.ActiveEmbeddingModel = "does-not-exist";

        var ex = Assert.Throws<InvalidOperationException>(settings.GetActiveEmbeddingModel);
        Assert.Contains("does-not-exist", ex.Message);
    }

    [Fact]
    public void GetActiveEmbeddingModelThrowsForEmptyRegistry()
    {
        var settings = new OnnxSettings();

        Assert.Throws<InvalidOperationException>(settings.GetActiveEmbeddingModel);
    }

    [Fact]
    public void GetActiveRerankerModelReturnsFirstEntryWhenSelectorEmpty()
    {
        var settings = BuildSettingsWithRerankerRegistry();
        settings.ActiveRerankerModel = string.Empty;

        var active = settings.GetActiveRerankerModel();

        Assert.NotNull(active);
        Assert.Equal("mxbai-base", active.Name);
    }

    [Fact]
    public void GetActiveRerankerModelReturnsNullForNoneSentinel()
    {
        var settings = BuildSettingsWithRerankerRegistry();
        settings.ActiveRerankerModel = OnnxSettings.RerankerNoneSentinel;

        var active = settings.GetActiveRerankerModel();

        Assert.Null(active);
    }

    [Fact]
    public void GetActiveRerankerModelReturnsNullForNoneSentinelCaseInsensitive()
    {
        var settings = BuildSettingsWithRerankerRegistry();
        settings.ActiveRerankerModel = "None";

        var active = settings.GetActiveRerankerModel();

        Assert.Null(active);
    }

    [Fact]
    public void GetActiveRerankerModelReturnsNamedEntry()
    {
        var settings = BuildSettingsWithRerankerRegistry();
        settings.ActiveRerankerModel = "mxbai-large";

        var active = settings.GetActiveRerankerModel();

        Assert.NotNull(active);
        Assert.Equal("mxbai-large", active.Name);
    }

    [Fact]
    public void GetActiveRerankerModelThrowsForInvalidName()
    {
        var settings = BuildSettingsWithRerankerRegistry();
        settings.ActiveRerankerModel = "typo";

        var ex = Assert.Throws<InvalidOperationException>(settings.GetActiveRerankerModel);
        Assert.Contains("typo", ex.Message);
        Assert.Contains(OnnxSettings.RerankerNoneSentinel, ex.Message);
    }

    [Fact]
    public void GetActiveRerankerModelReturnsNullForEmptyRegistry()
    {
        var settings = new OnnxSettings();

        var active = settings.GetActiveRerankerModel();

        Assert.Null(active);
    }

    [Fact]
    public void GetActiveRerankerModelThrowsForExplicitNameOnEmptyRegistry()
    {
        // Subtle edge case: registry is empty AND ActiveRerankerModel
        // is set to a non-empty, non-"none" value. The four-case switch
        // in GetActiveRerankerModel takes the (false, false) branch and
        // FirstOrDefault returns null over the empty list. Without the
        // throw the caller would think reranking is disabled when in
        // fact the operator asked for a specific entry that doesn't
        // exist. The throw routes the operator to the helpful error
        // message naming both fixes (registered name or "none").
        var settings = new OnnxSettings { ActiveRerankerModel = "mxbai" };

        var ex = Assert.Throws<InvalidOperationException>(settings.GetActiveRerankerModel);
        Assert.Contains("mxbai", ex.Message);
        Assert.Contains(OnnxSettings.RerankerNoneSentinel, ex.Message);
    }

    [Fact]
    public void EmbeddingModelEntryDefaultsMatchExpected()
    {
        var entry = new EmbeddingModelEntry();

        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(string.Empty, entry.Description);
        Assert.Equal(string.Empty, entry.RepoId);
        Assert.Equal(string.Empty, entry.ModelFile);
        Assert.Equal(TokenizerFamily.Bert, entry.TokenizerFamily);
        Assert.Equal(string.Empty, entry.VocabFile);
        Assert.Equal(string.Empty, entry.SpmFile);
        Assert.Equal(expected: 0, entry.Dimensions);
        Assert.Equal(DefaultMaxSequenceLength, entry.MaxSequenceLength);
        Assert.Equal(string.Empty, entry.DocumentPrefix);
        Assert.Equal(string.Empty, entry.QueryPrefix);
    }

    [Fact]
    public void RerankerModelEntryDefaultsMatchExpected()
    {
        var entry = new RerankerModelEntry();

        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(string.Empty, entry.Description);
        Assert.Equal(string.Empty, entry.RepoId);
        Assert.Equal(string.Empty, entry.ModelFile);
        Assert.Equal(TokenizerFamily.SentencePiece, entry.TokenizerFamily);
        Assert.Equal(string.Empty, entry.VocabFile);
        Assert.Equal(string.Empty, entry.SpmFile);
        Assert.Equal(DefaultMaxSequenceLength, entry.MaxSequenceLength);
        Assert.Empty(entry.SpecialTokens);
    }

    private static OnnxSettings BuildSettingsWithEmbeddingRegistry()
    {
        var settings = new OnnxSettings();
        settings.EmbeddingModels.Add(new EmbeddingModelEntry
                                         { Name = "nomic", Dimensions = 768, TokenizerFamily = TokenizerFamily.Bert });
        settings.EmbeddingModels.Add(new EmbeddingModelEntry
                                         { Name = "minilm", Dimensions = 384, TokenizerFamily = TokenizerFamily.Bert });
        return settings;
    }

    private static OnnxSettings BuildSettingsWithRerankerRegistry()
    {
        var settings = new OnnxSettings();
        settings.RerankerModels.Add(new RerankerModelEntry
                                        { Name = "mxbai-base", TokenizerFamily = TokenizerFamily.SentencePiece });
        settings.RerankerModels.Add(new RerankerModelEntry
                                        { Name = "mxbai-large", TokenizerFamily = TokenizerFamily.SentencePiece });
        return settings;
    }

    private const int DefaultMaxSequenceLength = 512;
}
