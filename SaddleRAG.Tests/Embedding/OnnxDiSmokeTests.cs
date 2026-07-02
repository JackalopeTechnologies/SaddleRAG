// OnnxDiSmokeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

/// <summary>
///     Smoke-tests the Program.cs DI conditional: when
///     <c>Onnx.Enabled &amp;&amp; Onnx.EmbeddingEnabled</c> is true the
///     registered <c>IEmbeddingProvider</c> is <c>OnnxEmbeddingProvider</c>;
///     otherwise it stays at <c>OllamaEmbeddingProvider</c>. Mirrors the
///     wiring in <c>SaddleRAG.Mcp.Program</c> so a regression there gets
///     caught without booting the full host.
/// </summary>
public sealed class OnnxDiSmokeTests
{
    [Fact]
    public void OnnxDisabledRegistersOllamaEmbeddingProvider()
    {
        var provider = BuildEmbeddingProvider(enabled: false, embeddingEnabled: false, includeFiles: false);

        Assert.IsType<OllamaEmbeddingProvider>(provider);
    }

    [Fact]
    public void OnnxEnabledButEmbeddingDisabledRegistersOllamaEmbeddingProvider()
    {
        var provider = BuildEmbeddingProvider(enabled: true, embeddingEnabled: false, includeFiles: false);

        Assert.IsType<OllamaEmbeddingProvider>(provider);
    }

    [Fact]
    public void OnnxEnabledAndEmbeddingEnabledRegistersOnnxEmbeddingProvider()
    {
        Assert.SkipUnless(File.Exists(NomicModelPath) && File.Exists(NomicVocabPath),
                          $"Nomic ONNX model not staged; run the Phase 1 spike to populate.");

        var provider = BuildEmbeddingProvider(enabled: true, embeddingEnabled: true, includeFiles: true);

        Assert.IsType<OnnxEmbeddingProvider>(provider);
        Assert.Equal("onnx", provider.ProviderId);
        Assert.Equal("nomic-embed-text-v1.5", provider.ModelName);
    }

    [Fact]
    public void ClassifierBindingResolvesToBackendSwitch()
    {
        var classifier = BuildClassifier();

        Assert.IsType<ClassifierBackendSwitch>(classifier);
    }

    [Fact]
    public void BackendSwitchDefaultsToOnnxBackend()
    {
        var classifier = (ClassifierBackendSwitch) BuildClassifier();

        Assert.Equal("onnx", classifier.ActiveBackendName);
    }

    /// <summary>
    ///     Mirrors the classifier DI wiring in <c>SaddleRAG.Mcp.Program</c>:
    ///     the ONNX generator loads lazily so no model files are required to
    ///     build the graph, and <see cref="ILlmClassifier" /> resolves to the
    ///     <see cref="ClassifierBackendSwitch" /> composing ONNX (default),
    ///     Ollama, and the probe. A regression in that wiring (e.g. a DI cycle
    ///     from binding the switch's Ollama arg to the ILlmClassifier seam, or
    ///     the generator loading eagerly) gets caught here without booting the
    ///     host or staging a model.
    /// </summary>
    private static ILlmClassifier BuildClassifier()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLogging();
        services.AddHttpClient();

        services.Configure<OllamaSettings>(opts =>
        {
            opts.Endpoint = "http://localhost:11434";
        });

        services.Configure<OnnxSettings>(opts =>
        {
            opts.Enabled = true;
            opts.ModelsDir = Path.GetTempPath();
            opts.ExecutionProvider = OnnxExecutionProvider.Cpu;
        });

        services.AddSingleton<OllamaLlmClassifier>();

        services.AddSingleton<OnnxClassifierGenerator>(sp =>
        {
            var onnxSettings = sp.GetRequiredService<IOptions<OnnxSettings>>().Value;
            var entry = ClassifierEntryResolver.Resolve(onnxSettings, onnxSettings.ExecutionProvider);
            string modelFolder = Path.Combine(onnxSettings.ModelsDir, entry.Name);
            return new OnnxClassifierGenerator(modelFolder, entry);
        });

        services.AddSingleton<OnnxLlmClassifier>(sp =>
            new OnnxLlmClassifier(new SerializedClassifierGenerator(sp.GetRequiredService<OnnxClassifierGenerator>()),
                                  sp.GetRequiredService<ILogger<OnnxLlmClassifier>>()
                                 )
        );

        services.AddSingleton<IOllamaProbe>(sp =>
        {
            var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
            var ollamaSettings = sp.GetRequiredService<IOptions<OllamaSettings>>().Value;
            return new OllamaProbe(httpFactory.CreateClient(), ollamaSettings);
        });

        services.AddSingleton<ILlmClassifier>(sp =>
            new ClassifierBackendSwitch(sp.GetRequiredService<OnnxLlmClassifier>(),
                                        sp.GetRequiredService<OllamaLlmClassifier>(),
                                        sp.GetRequiredService<IOllamaProbe>(),
                                        sp.GetRequiredService<ILogger<ClassifierBackendSwitch>>()
                                       )
        );

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ILlmClassifier>();
    }

    private static IEmbeddingProvider BuildEmbeddingProvider(bool enabled, bool embeddingEnabled, bool includeFiles)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddLogging();

        services.Configure<OllamaSettings>(opts =>
        {
            opts.Endpoint = "http://localhost:11434";
            opts.EmbeddingModel = "nomic-embed-text";
            opts.EmbeddingDimensions = 768;
        });

        services.Configure<OnnxSettings>(opts =>
        {
            opts.Enabled = enabled;
            opts.EmbeddingEnabled = embeddingEnabled;
            opts.ModelsDir = includeFiles ? ScratchModelsRoot : Path.GetTempPath();
            opts.ActiveEmbeddingModel = "nomic-embed-text-v1.5";
            opts.GraphOptimizationLevel = OnnxGraphOptimizationLevel.Basic;
            opts.EmbeddingModels.Add(new EmbeddingModelEntry
                                         {
                                             Name = "nomic-embed-text-v1.5",
                                             RepoId = "nomic-ai/nomic-embed-text-v1.5",
                                             ModelFile = "onnx/model_fp16.onnx",
                                             TokenizerFamily = TokenizerFamily.Bert,
                                             VocabFile = "vocab.txt",
                                             Dimensions = 768,
                                             MaxSequenceLength = 512,
                                             DocumentPrefix = "search_document: ",
                                             QueryPrefix = "search_query: "
                                         });
        });

        services.AddSingleton<OnnxRuntimeCapabilities>();

        // Mirror Program.cs's conditional registration.
        var onnxSettings = new OnnxSettings { Enabled = enabled, EmbeddingEnabled = embeddingEnabled };
        if (onnxSettings.Enabled && onnxSettings.EmbeddingEnabled)
            services.AddSingleton<IEmbeddingProvider, OnnxEmbeddingProvider>();
        else
            services.AddSingleton<IEmbeddingProvider, OllamaEmbeddingProvider>();

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IEmbeddingProvider>();
    }

    private static string LocateScratchRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, RepoMarker)))
            current = current.Parent;
        string root = current?.FullName ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, "Scratch", "onnx-spike", "models");
    }

    private static readonly string ScratchModelsRoot = LocateScratchRoot();

    private static string NomicModelPath => Path.Combine(ScratchModelsRoot, "nomic-embed-text-v1.5", "model.onnx");
    private static string NomicVocabPath => Path.Combine(ScratchModelsRoot, "nomic-embed-text-v1.5", "vocab.txt");

    private const string RepoMarker = "SaddleRAG.slnx";
}
