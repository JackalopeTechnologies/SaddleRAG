// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Tests.Installer;

/// <summary>Exercises startup model selection using the configuration shipped to new installations.</summary>
public sealed class NewInstallationModelDefaultsTests
{
    [Theory]
    [InlineData("SaddleRAG.Mcp", false)]
    [InlineData("SaddleRAG.Mcp", true)]
    [InlineData("SaddleRAG.Cli", false)]
    public async Task PackagedDefaultsRequireOnlyOllamaModelsAndLoadNoNativeSorter(string project, bool development)
    {
        using var configuration = LoadConfiguration(project, development);
        var ollama = configuration.GetSection(OllamaSettings.SectionName).Get<OllamaSettings>();
        var onnx = configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>() ?? new OnnxSettings();
        var ranking = configuration.GetSection(RankingSettings.SectionName).Get<RankingSettings>() ?? new RankingSettings();
        Assert.NotNull(ollama);
        Assert.Equal(ClassifierBackendNames.Ollama, configuration[ClassifierBackendSwitch.BackendConfigurationPath]);
        Assert.False(onnx.Enabled && onnx.EmbeddingEnabled);
        Assert.Equal(ReRankerStrategy.Off, ranking.ReRankerStrategy);
        Assert.True(ollama.EmbeddingUseTaskPrefixes);
        Assert.Equal(768, ollama.EmbeddingDimensions);
        Assert.Equal("phi4-mini:3.8b", ollama.GetActiveReconModel().Name);
        var required = OllamaBootstrapper.ResolveRequiredModels(ollama, additionalModels: null, ollamaClassifierActive: true);
        Assert.Equal(new[] { "embeddinggemma-2:270m", "phi4-mini:3.8b" }, required.OrderBy(name => name));
        Assert.Single(configuration.GetSection("MongoDB:Profiles").GetChildren());
        Assert.True(configuration.GetSection("MongoDB:Profiles:local").Exists());

        string modelDirectory = Path.Combine(Path.GetTempPath(), $"saddlerag-default-models-{Guid.NewGuid():N}");
        onnx.ModelsDir = modelDirectory;
        try
        {
            var factory = Substitute.For<IHttpClientFactory>();
            factory.CreateClient(Arg.Any<string>()).Returns(_ =>
                throw new InvalidOperationException("New installations must not download native models."));
            var options = Options.Create(onnx);
            var downloader = new OnnxModelDownloader(factory, options, NullLogger<OnnxModelDownloader>.Instance);
            await downloader.EnsureActiveModelsAsync(TestContext.Current.CancellationToken);
            factory.DidNotReceive().CreateClient(Arg.Any<string>());

            using var sorter = new OnnxReRanker(options, new OnnxRuntimeCapabilities(), NullLogger<OnnxReRanker>.Instance);
            Assert.Empty(sorter.ModelName);
            Assert.Empty(Directory.Exists(modelDirectory) ? Directory.EnumerateFileSystemEntries(modelDirectory) : []);
        }
        finally
        {
            if (Directory.Exists(modelDirectory))
                Directory.Delete(modelDirectory, recursive: true);
        }
    }

    [Fact]
    public void PackagedRegistryStillAllowsExplicitDirectMlEmbeddingAndSorterSelection()
    {
        using var configuration = LoadConfiguration("SaddleRAG.Mcp", development: false);
        var onnx = configuration.GetSection(OnnxSettings.SectionName).Get<OnnxSettings>();
        Assert.NotNull(onnx);
        onnx.EmbeddingEnabled = true;
        onnx.ActiveRerankerModel = "mxbai-rerank-base-v1";
        onnx.ExecutionProvider = OnnxExecutionProvider.DirectMl;

        var validation = new OnnxSettingsValidator().Validate(name: null, onnx);
        Assert.True(validation.Succeeded, validation.FailureMessage);
        Assert.True(onnx.Enabled);
        Assert.Equal("nomic-embed-text-v1.5", onnx.GetActiveEmbeddingModel().Name);
        Assert.Equal("mxbai-rerank-base-v1", onnx.GetActiveRerankerModel()?.Name);
    }

    private static ConfigurationManager LoadConfiguration(string project, bool development)
    {
        ArgumentException.ThrowIfNullOrEmpty(project);
        string? root = InstallerSourceTreeResolver.TryResolveRepositoryRoot();
        Assert.NotNull(root);
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(Path.Combine(root, project, "appsettings.json"));
        if (development)
            configuration.AddJsonFile(Path.Combine(root, project, "appsettings.Development.json"));
        return configuration;
    }
}
