// OllamaSettingsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OllamaSettingsTests
{
    [Fact]
    public void DefaultsMatchExpected()
    {
        var settings = new OllamaSettings();

        Assert.Equal(OllamaSettings.DefaultEndpoint, settings.Endpoint);
        Assert.Equal(OllamaSettings.DefaultEmbeddingModel, settings.EmbeddingModel);
        Assert.Equal(OllamaSettings.DefaultEmbeddingDimensions, settings.EmbeddingDimensions);
        Assert.Equal(string.Empty, settings.ActiveClassificationModel);
        Assert.Equal(string.Empty, settings.ActiveReconModel);
        Assert.Empty(settings.ClassificationModels);
        Assert.Empty(settings.ReconModels);
        Assert.Equal(OllamaSettings.DefaultReconMinConfidence, settings.ReconMinConfidence);
    }

    [Fact]
    public void GetActiveClassificationModelReturnsFirstEntryWhenSelectorEmpty()
    {
        var settings = new OllamaSettings();
        settings.ClassificationModels.Add(new OllamaModelEntry { Name = "phi4-mini:3.8b" });
        settings.ClassificationModels.Add(new OllamaModelEntry { Name = "llama3.2:3b" });

        var active = settings.GetActiveClassificationModel();

        Assert.Equal("phi4-mini:3.8b", active.Name);
    }

    [Fact]
    public void GetActiveClassificationModelReturnsNamedEntry()
    {
        var settings = new OllamaSettings { ActiveClassificationModel = "llama3.2:3b" };
        settings.ClassificationModels.Add(new OllamaModelEntry { Name = "phi4-mini:3.8b" });
        settings.ClassificationModels.Add(new OllamaModelEntry { Name = "llama3.2:3b" });

        var active = settings.GetActiveClassificationModel();

        Assert.Equal("llama3.2:3b", active.Name);
    }

    [Fact]
    public void GetActiveClassificationModelThrowsForInvalidName()
    {
        var settings = new OllamaSettings { ActiveClassificationModel = "does-not-exist" };
        settings.ClassificationModels.Add(new OllamaModelEntry { Name = "phi4-mini:3.8b" });

        var ex = Assert.Throws<InvalidOperationException>(settings.GetActiveClassificationModel);
        Assert.Contains("does-not-exist", ex.Message);
    }

    [Fact]
    public void GetActiveClassificationModelThrowsForEmptyRegistry()
    {
        var settings = new OllamaSettings();

        Assert.Throws<InvalidOperationException>(settings.GetActiveClassificationModel);
    }

    [Fact]
    public void GetActiveReconModelReturnsFirstEntryWhenSelectorEmpty()
    {
        var settings = new OllamaSettings();
        settings.ReconModels.Add(new OllamaModelEntry { Name = "phi4:14b" });

        var active = settings.GetActiveReconModel();

        Assert.Equal("phi4:14b", active.Name);
    }

    [Fact]
    public void GetActiveReconModelThrowsForEmptyRegistry()
    {
        var settings = new OllamaSettings();

        Assert.Throws<InvalidOperationException>(settings.GetActiveReconModel);
    }

    [Fact]
    public void OllamaModelEntryDefaultsAreEmptyStrings()
    {
        var entry = new OllamaModelEntry();

        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(string.Empty, entry.Description);
    }
}
