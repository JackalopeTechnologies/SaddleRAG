// ConfigPageInitializationTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Reflection;
using SaddleRAG.Monitor.Pages;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     Pins the ConfigPage initialization contract: <c>OnInitializedAsync</c>
///     resolves <see cref="IMonitorConfigSource" /> and stores the snapshot
///     for the Razor view. Tests use the established TestableXxxPage
///     pattern (no bunit) to call the protected OnInitializedAsync
///     directly and observe the resulting Snapshot.
/// </summary>
public sealed class ConfigPageInitializationTests
{
    private sealed class TestableConfigPage : ConfigPageBase
    {
        public TestableConfigPage(IMonitorConfigSource source)
        {
            var prop = typeof(ConfigPageBase)
                       .GetProperty(ConfigSourcePropertyName, BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException("ConfigSource property missing on ConfigPageBase");
            prop.SetValue(this, source);
        }

        public MonitorConfigSnapshot? SnapshotForTest => Snapshot;

        public Task InitializeForTestAsync()
        {
            var method = typeof(ConfigPageBase)
                         .GetMethod(InitMethodName, BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException("OnInitializedAsync method missing on ConfigPageBase");
            var invoked = method.Invoke(this, [])
                          ?? throw new InvalidOperationException("OnInitializedAsync returned null Task");
            return (Task) invoked;
        }

        private const string ConfigSourcePropertyName = "ConfigSource";
        private const string InitMethodName = "OnInitializedAsync";
    }

    private static MonitorConfigSnapshot NewSnapshot() =>
        new(Embedding: new MonitorConfigEmbedding("stub", "stub-model", 4, OnnxBacked: false, OnnxEmbeddingEnabled: false),
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
    public async Task OnInitializedAsyncStoresSnapshotFromConfigSource()
    {
        var snapshot = NewSnapshot();
        var source = Substitute.For<IMonitorConfigSource>();
        source.GetSnapshot().Returns(snapshot);
        var page = new TestableConfigPage(source);

        await page.InitializeForTestAsync();

        Assert.Same(snapshot, page.SnapshotForTest);
        source.Received(1).GetSnapshot();
    }
}
