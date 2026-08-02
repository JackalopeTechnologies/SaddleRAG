// OllamaBootstrapperResolveRequiredModelsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OllamaBootstrapperResolveRequiredModelsTests
{
    [Fact]
    public void IncludesEmbeddingAndClassificationModels()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: "phi4-mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: null,
                                                                ollamaClassifierActive: true);

        Assert.Contains("nomic-embed", required);
        Assert.Contains("phi4-mini", required);
    }

    [Fact]
    public void SkipsClassificationModelWhenNameEmpty()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: string.Empty);

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: null,
                                                                ollamaClassifierActive: true);

        Assert.Contains("nomic-embed", required);
        Assert.Single(required);
    }

    [Fact]
    public void IncludesAdditionalModels()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: "phi4-mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: ["llama3.2:3b", "qwen2.5-coder"],
                                                                ollamaClassifierActive: true);

        Assert.Contains("llama3.2:3b", required);
        Assert.Contains("qwen2.5-coder", required);
        Assert.Equal(expected: 4, required.Count);
    }

    [Fact]
    public void DedupesCaseInsensitively()
    {
        var settings = MakeSettings(embedding: "NOMIC-EMBED", classification: "Phi4-Mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: ["nomic-embed", "PHI4-MINI"],
                                                                ollamaClassifierActive: true);

        Assert.Equal(expected: 2, required.Count);
    }

    [Fact]
    public void SkipsNullOrEmptyAdditionalEntries()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: "phi4-mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: ["llama3.2", string.Empty, "qwen"],
                                                                ollamaClassifierActive: true);

        Assert.Contains("llama3.2", required);
        Assert.Contains("qwen", required);
        Assert.DoesNotContain(string.Empty, required);
        Assert.Equal(expected: 4, required.Count);
    }

    // The regression this gate exists for: the bootstrap also runs on the
    // Ollama-embedding path, where the classifier may be ONNX. Pulling the
    // Ollama classification model there costs a multi-gigabyte download for a
    // model nothing will consume.
    [Fact]
    public void OmitsClassificationModelWhenOllamaClassifierIsNotActive()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: "phi4-mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: null,
                                                                ollamaClassifierActive: false);

        Assert.Contains("nomic-embed", required);
        Assert.DoesNotContain("phi4-mini", required);
        Assert.Single(required);
    }

    // The embedding model and any explicitly requested extras are unrelated to
    // which classifier backend is live, so the gate must not touch them.
    [Fact]
    public void GateLeavesEmbeddingAndAdditionalModelsAlone()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: "phi4-mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: ["llama3.2:3b"],
                                                                ollamaClassifierActive: false);

        Assert.Contains("nomic-embed", required);
        Assert.Contains("llama3.2:3b", required);
        Assert.DoesNotContain("phi4-mini", required);
        Assert.Equal(expected: 2, required.Count);
    }

    // A plain classifier is not a backend switch, so it is never Ollama-backed.
    [Fact]
    public void IsOllamaActiveIsFalseForNullOrNonSwitchClassifier()
    {
        Assert.False(ClassifierBackendSwitch.IsOllamaActive(classifier: null));
        Assert.False(ClassifierBackendSwitch.IsOllamaActive(Substitute.For<ILlmClassifier>()));
    }

    // Always seeds at least one entry so GetActiveClassificationModel
    // doesn't throw — the empty-name case still exercises the
    // !string.IsNullOrEmpty guard inside ResolveRequiredModels.
    private static OllamaSettings MakeSettings(string embedding, string classification)
    {
        var settings = new OllamaSettings
                           {
                               EmbeddingModel = embedding,
                               ActiveClassificationModel = string.Empty
                           };
        settings.ClassificationModels =
            [
                new OllamaModelEntry
                    {
                        Name = classification,
                        Description = "test"
                    }
            ];
        return settings;
    }
}
