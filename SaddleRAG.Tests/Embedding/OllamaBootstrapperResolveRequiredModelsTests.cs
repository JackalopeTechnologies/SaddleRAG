// OllamaBootstrapperResolveRequiredModelsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OllamaBootstrapperResolveRequiredModelsTests
{
    [Fact]
    public void IncludesEmbeddingAndClassificationModels()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: "phi4-mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings, additionalModels: null);

        Assert.Contains("nomic-embed", required);
        Assert.Contains("phi4-mini", required);
    }

    [Fact]
    public void SkipsClassificationModelWhenNameEmpty()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: string.Empty);

        var required = OllamaBootstrapper.ResolveRequiredModels(settings, additionalModels: null);

        Assert.Contains("nomic-embed", required);
        Assert.Single(required);
    }

    [Fact]
    public void IncludesAdditionalModels()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: "phi4-mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: ["llama3.2:3b", "qwen2.5-coder"]);

        Assert.Contains("llama3.2:3b", required);
        Assert.Contains("qwen2.5-coder", required);
        Assert.Equal(expected: 4, required.Count);
    }

    [Fact]
    public void DedupesCaseInsensitively()
    {
        var settings = MakeSettings(embedding: "NOMIC-EMBED", classification: "Phi4-Mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: ["nomic-embed", "PHI4-MINI"]);

        Assert.Equal(expected: 2, required.Count);
    }

    [Fact]
    public void SkipsNullOrEmptyAdditionalEntries()
    {
        var settings = MakeSettings(embedding: "nomic-embed", classification: "phi4-mini");

        var required = OllamaBootstrapper.ResolveRequiredModels(settings,
                                                                additionalModels: ["llama3.2", string.Empty, "qwen"]);

        Assert.Contains("llama3.2", required);
        Assert.Contains("qwen", required);
        Assert.DoesNotContain(string.Empty, required);
        Assert.Equal(expected: 4, required.Count);
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
