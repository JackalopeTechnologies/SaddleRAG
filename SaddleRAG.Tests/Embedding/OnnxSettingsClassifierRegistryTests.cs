// OnnxSettingsClassifierRegistryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.Extensions.Options;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Covers the <c>ClassifierModels</c> registry and
///     <c>ActiveClassifierModel</c> selector on <see cref="OnnxSettings" />,
///     including the default-to-first fallback and invalid-name rejection,
///     mirroring the embedding and reranker registry tests.
/// </summary>
public sealed class OnnxSettingsClassifierRegistryTests
{
    #region OnnxSettings.GetActiveClassifierModel tests

    [Fact]
    public void GetActiveClassifierModelReturnsFirstEntryWhenSelectorEmpty()
    {
        var settings = BuildSettingsWithClassifierRegistry();
        settings.ActiveClassifierModel = string.Empty;

        var active = settings.GetActiveClassifierModel();

        Assert.Equal(OnnxSettings.Phi4MiniCpuName, active.Name);
    }

    [Fact]
    public void GetActiveClassifierModelReturnsNamedEntry()
    {
        var settings = BuildSettingsWithClassifierRegistry();
        settings.ActiveClassifierModel = OnnxSettings.Phi4MiniDirectMlName;

        var active = settings.GetActiveClassifierModel();

        Assert.Equal(OnnxSettings.Phi4MiniDirectMlName, active.Name);
    }

    [Fact]
    public void GetActiveClassifierModelThrowsForInvalidName()
    {
        var settings = BuildSettingsWithClassifierRegistry();
        settings.ActiveClassifierModel = "does-not-exist";

        var ex = Assert.Throws<InvalidOperationException>(settings.GetActiveClassifierModel);
        Assert.Contains("does-not-exist", ex.Message);
    }

    [Fact]
    public void GetActiveClassifierModelThrowsForEmptyRegistry()
    {
        var settings = new OnnxSettings { ClassifierModels = [] };

        Assert.Throws<InvalidOperationException>(settings.GetActiveClassifierModel);
    }

    #endregion

    #region OnnxSettingsValidator classifier tests

    [Fact]
    public void ValidatorRejectsActiveClassifierModelNotInRegistry()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings
                           {
                               Enabled = true,
                               ActiveClassifierModel = "typo-classifier",
                               ClassifierModels = []
                           };
        settings.ClassifierModels.Add(MakeCpuEntry(OnnxSettings.Phi4MiniCpuName));

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Failed);
        Assert.Contains("typo-classifier", result.FailureMessage);
        Assert.Contains("ActiveClassifierModel", result.FailureMessage);
    }

    [Fact]
    public void ValidatorAcceptsEmptyActiveClassifierModelWithNonEmptyRegistry()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true, ClassifierModels = [] };
        settings.ClassifierModels.Add(MakeCpuEntry(OnnxSettings.Phi4MiniCpuName));
        settings.EmbeddingModels.Add(MinimalEmbeddingEntry());

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidatorRejectsClassifierEntryWithEmptyName()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true };
        settings.ClassifierModels.Add(new ClassifierModelEntry
                                          {
                                              Name = string.Empty,
                                              RepoId = "microsoft/Phi-4-mini-instruct-onnx",
                                              ModelFolder = "cpu"
                                          });

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Failed);
        Assert.Contains("empty Name", result.FailureMessage);
    }

    [Fact]
    public void ValidatorRejectsClassifierEntryWithEmptyRepoId()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true, ClassifierModels = [] };
        settings.ClassifierModels.Add(new ClassifierModelEntry
                                          {
                                              Name = OnnxSettings.Phi4MiniCpuName,
                                              RepoId = string.Empty,
                                              ModelFolder = OnnxSettings.Phi4MiniCpuFolder
                                          });

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Failed);
        Assert.Contains("RepoId", result.FailureMessage);
        Assert.Contains(OnnxSettings.Phi4MiniCpuName, result.FailureMessage);
    }

    [Fact]
    public void ValidatorRejectsClassifierEntryWithEmptyModelFolder()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true, ClassifierModels = [] };
        settings.ClassifierModels.Add(new ClassifierModelEntry
                                          {
                                              Name = OnnxSettings.Phi4MiniCpuName,
                                              RepoId = OnnxSettings.Phi4MiniRepoId,
                                              ModelFolder = string.Empty
                                          });

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Failed);
        Assert.Contains("ModelFolder", result.FailureMessage);
        Assert.Contains(OnnxSettings.Phi4MiniCpuName, result.FailureMessage);
    }

    [Fact]
    public void ValidatorRejectsDuplicateClassifierNames()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true, ClassifierModels = [] };
        settings.ClassifierModels.Add(MakeCpuEntry("duplicate-classifier"));
        settings.ClassifierModels.Add(MakeCpuEntry("duplicate-classifier"));

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Failed);
        Assert.Contains("duplicate-classifier", result.FailureMessage);
        Assert.Contains("ClassifierModels", result.FailureMessage);
    }

    [Fact]
    public void ValidatorAcceptsWellFormedClassifierRegistryWithoutActiveSelector()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings { Enabled = true, ClassifierModels = [] };
        settings.ClassifierModels.Add(MakeCpuEntry(OnnxSettings.Phi4MiniCpuName));
        settings.ClassifierModels.Add(MakeGpuEntry(OnnxSettings.Phi4MiniDirectMlName));
        settings.EmbeddingModels.Add(MinimalEmbeddingEntry());

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ValidatorAcceptsWellFormedClassifierRegistryWithMatchingActiveSelector()
    {
        var validator = new OnnxSettingsValidator();
        var settings = new OnnxSettings
                           {
                               Enabled = true,
                               ActiveClassifierModel = OnnxSettings.Phi4MiniDirectMlName,
                               ClassifierModels = []
                           };
        settings.ClassifierModels.Add(MakeCpuEntry(OnnxSettings.Phi4MiniCpuName));
        settings.ClassifierModels.Add(MakeGpuEntry(OnnxSettings.Phi4MiniDirectMlName));
        settings.EmbeddingModels.Add(MinimalEmbeddingEntry());

        var result = validator.Validate(name: null, settings);

        Assert.True(result.Succeeded);
    }

    #endregion

    #region ClassifierModelEntry defaults tests

    [Fact]
    public void ClassifierModelEntryDefaultsMatchExpected()
    {
        var entry = new ClassifierModelEntry();

        Assert.Equal(string.Empty, entry.Name);
        Assert.Equal(string.Empty, entry.Description);
        Assert.Equal(string.Empty, entry.RepoId);
        Assert.Equal(string.Empty, entry.ModelFolder);
        Assert.Equal(DefaultMaxContextLength, entry.MaxContextLength);
        Assert.Equal(DefaultMaxOutputTokens, entry.MaxOutputTokens);
        Assert.Equal(DefaultTemperature, entry.Temperature);
        Assert.Equal(DefaultStop, entry.Stop);
    }

    #endregion

    #region OnnxSettings defaults test

    [Fact]
    public void OnnxSettingsClassifierDefaultsMatchExpected()
    {
        var settings = new OnnxSettings();

        Assert.Equal(string.Empty, settings.ActiveClassifierModel);
        Assert.Equal(expected: 3, settings.ClassifierModels.Count);
        Assert.Equal(OnnxSettings.Phi4MiniCpuName, settings.ClassifierModels[index: 0].Name);
        Assert.Equal(OnnxSettings.Phi4MiniDirectMlName, settings.ClassifierModels[index: 1].Name);
        Assert.Equal(OnnxSettings.Phi4MiniCudaName, settings.ClassifierModels[index: 2].Name);
    }

    #endregion

    #region Helpers

    private static OnnxSettings BuildSettingsWithClassifierRegistry()
    {
        var settings = new OnnxSettings { ClassifierModels = [] };
        settings.ClassifierModels.Add(MakeCpuEntry(OnnxSettings.Phi4MiniCpuName));
        settings.ClassifierModels.Add(MakeGpuEntry(OnnxSettings.Phi4MiniDirectMlName));
        return settings;
    }

    private static ClassifierModelEntry MakeCpuEntry(string name) =>
        new()
        {
            Name = name,
            RepoId = OnnxSettings.Phi4MiniRepoId,
            // TODO(3.3): verify exact HF subfolder
            ModelFolder = OnnxSettings.Phi4MiniCpuFolder
        };

    private static ClassifierModelEntry MakeGpuEntry(string name) =>
        new()
        {
            Name = name,
            RepoId = OnnxSettings.Phi4MiniRepoId,
            // TODO(3.3): verify exact HF subfolder
            ModelFolder = OnnxSettings.Phi4MiniDirectMlFolder
        };

    private static EmbeddingModelEntry MinimalEmbeddingEntry() =>
        new()
        {
            Name = "nomic",
            RepoId = "nomic-ai/nomic-embed-text-v1.5",
            ModelFile = "onnx/model.onnx",
            TokenizerFamily = SaddleRAG.Core.Enums.TokenizerFamily.Bert,
            VocabFile = "vocab.txt"
        };

    private const int DefaultMaxContextLength = 4096;
    private const int DefaultMaxOutputTokens = 256;
    private const float DefaultTemperature = 0.0f;
    private const string DefaultStop = "</json>";

    #endregion
}
