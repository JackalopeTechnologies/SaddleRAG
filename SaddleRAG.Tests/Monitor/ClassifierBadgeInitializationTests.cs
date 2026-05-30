// ClassifierBadgeInitializationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Reflection;
using SaddleRAG.Monitor.Components;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     Pins the ClassifierBadge initialization contract: <c>OnInitialized</c>
///     resolves <see cref="IMonitorConfigSource" /> and stores the classifier
///     snapshot for the Razor view. Uses the established TestableXxxPage
///     reflection pattern (no bUnit) to inject the private
///     <c>ConfigSource</c> property and call the protected <c>OnInitialized</c>
///     directly.
/// </summary>
public sealed class ClassifierBadgeInitializationTests
{
    private sealed class TestableClassifierBadge : ClassifierBadge
    {
        public TestableClassifierBadge(IMonitorConfigSource source)
        {
            var prop = typeof(ClassifierBadge)
                       .GetProperty(ConfigSourcePropertyName, BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException("ConfigSource property missing on ClassifierBadge");
            prop.SetValue(this, source);
        }

        public void InitializeForTest()
        {
            var method = typeof(ClassifierBadge)
                         .GetMethod(InitMethodName, BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException("OnInitialized method missing on ClassifierBadge");
            method.Invoke(this, []);
        }

        public MonitorConfigClassifier? ClassifierForTest
        {
            get
            {
                var reflectedField = typeof(ClassifierBadge)
                                     .GetField(ClassifierFieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                                     ?? throw new InvalidOperationException("mClassifier field missing on ClassifierBadge");
                return (MonitorConfigClassifier?) reflectedField.GetValue(this);
            }
        }

        private const string ConfigSourcePropertyName = "ConfigSource";
        private const string InitMethodName = "OnInitialized";
        private const string ClassifierFieldName = "mClassifier";
    }

    private static MonitorConfigSnapshot NewOnnxSnapshot() =>
        new(Classifier: new MonitorConfigClassifier("onnx", "phi-3-mini-4k-instruct-cpu",
                                                    "microsoft/Phi-3-mini-4k-instruct-onnx",
                                                    "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4",
                                                    ModelFilesPresent: true,
                                                    OllamaClassificationModel: "phi4-mini:3.8b"
                                                   ),
            Embedding: new MonitorConfigEmbedding("stub", "stub-model", 4, OnnxBacked: false, OnnxEmbeddingEnabled: false),
            Reranker: new MonitorConfigReranker("Off", ActiveModel: null, OnnxEnabled: false),
            ExecutionProvider: new MonitorConfigExecutionProvider("Cpu", "Cpu", MatchesRequested: true,
                                                                  CompiledInProviders: ["Cpu"],
                                                                  LastLoadWarning: null
                                                                 ),
            Mongo: new MonitorConfigMongo("(direct)", "mongodb://localhost", "saddlerag", CredentialsPresent: false),
            Ollama: new MonitorConfigOllama("http://localhost:11434", "llama3", "llama3", "nomic-embed-text"),
            Profile: new MonitorConfigProfile("(direct)")
           );

    private static MonitorConfigSnapshot NewOllamaSnapshot() =>
        new(Classifier: new MonitorConfigClassifier("ollama", "phi-3-mini-4k-instruct-cpu",
                                                    "microsoft/Phi-3-mini-4k-instruct-onnx",
                                                    "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4",
                                                    ModelFilesPresent: false,
                                                    OllamaClassificationModel: "phi4-mini:3.8b"
                                                   ),
            Embedding: new MonitorConfigEmbedding("stub", "stub-model", 4, OnnxBacked: false, OnnxEmbeddingEnabled: false),
            Reranker: new MonitorConfigReranker("Off", ActiveModel: null, OnnxEnabled: false),
            ExecutionProvider: new MonitorConfigExecutionProvider("Cpu", "Cpu", MatchesRequested: true,
                                                                  CompiledInProviders: ["Cpu"],
                                                                  LastLoadWarning: null
                                                                 ),
            Mongo: new MonitorConfigMongo("(direct)", "mongodb://localhost", "saddlerag", CredentialsPresent: false),
            Ollama: new MonitorConfigOllama("http://localhost:11434", "llama3", "llama3", "nomic-embed-text"),
            Profile: new MonitorConfigProfile("(direct)")
           );

    [Fact]
    public void OnInitializedStoresClassifierFromConfigSource()
    {
        var snapshot = NewOnnxSnapshot();
        var source = Substitute.For<IMonitorConfigSource>();
        source.GetSnapshot().Returns(snapshot);
        var badge = new TestableClassifierBadge(source);

        badge.InitializeForTest();

        Assert.Same(snapshot.Classifier, badge.ClassifierForTest);
        source.Received(1).GetSnapshot();
    }

    [Fact]
    public void OnInitializedWithOnnxBackendResolvesOnnxModel()
    {
        var snapshot = NewOnnxSnapshot();
        var source = Substitute.For<IMonitorConfigSource>();
        source.GetSnapshot().Returns(snapshot);
        var badge = new TestableClassifierBadge(source);

        badge.InitializeForTest();

        Assert.Equal("onnx", badge.ClassifierForTest?.ActiveBackend);
        Assert.Equal("phi-3-mini-4k-instruct-cpu", badge.ClassifierForTest?.ActiveOnnxModel);
    }

    [Fact]
    public void OnInitializedWithOllamaBackendResolvesOllamaModel()
    {
        var snapshot = NewOllamaSnapshot();
        var source = Substitute.For<IMonitorConfigSource>();
        source.GetSnapshot().Returns(snapshot);
        var badge = new TestableClassifierBadge(source);

        badge.InitializeForTest();

        Assert.Equal("ollama", badge.ClassifierForTest?.ActiveBackend);
        Assert.Equal("phi4-mini:3.8b", badge.ClassifierForTest?.OllamaClassificationModel);
    }
}
