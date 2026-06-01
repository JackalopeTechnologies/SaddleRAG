// ClassifierBackendSwitchTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging.Abstractions;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;

#endregion

namespace SaddleRAG.Tests.Classification;

/// <summary>
///     Covers <see cref="ClassifierBackendSwitch" />: default backend
///     selection, runtime swap to Ollama, Ollama-unreachable error path, and
///     swap back to ONNX. Uses fakes for both backends and the probe so
///     no real HTTP or ONNX runtime is required.
/// </summary>
public sealed class ClassifierBackendSwitchTests
{
    #region Fakes

    private sealed class FakeClassifier : ILlmClassifier
    {
        public int CallCount { get; private set; }
        public DocCategory ReturnCategory { get; init; } = DocCategory.Overview;
        public float ReturnConfidence { get; init; } = 0.9f;
        public string BackendName { get; init; } = "fake";
        public string ModelId { get; init; } = string.Empty;

        public void IncrementCallCount() => CallCount++;

        public Task<(DocCategory Category, float Confidence)> ClassifyAsync(PageRecord page,
                                                                            string libraryHint,
                                                                            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult((ReturnCategory, ReturnConfidence));
        }

        public string GetCurrentVersion() => $"{ModelId}-v1";
    }

    private sealed class FakeOllamaProbe : IOllamaProbe
    {
        public bool Reachable { get; init; } = true;

        public Task<bool> IsReachableAsync(CancellationToken ct = default) =>
            Task.FromResult(Reachable);
    }

    #endregion

    #region Helpers

    private static OnnxLlmClassifier NewOnnxClassifier(FakeClassifier? fake = null)
    {
        // OnnxLlmClassifier wraps IClassifierGenerator; we supply one that
        // delegates to our FakeClassifier so we can track which backend was called.
        var generator = fake != null
                            ? (IClassifierGenerator) new BridgeGenerator(fake)
                            : new BridgeGenerator(new FakeClassifier());
        return new OnnxLlmClassifier(generator, NullLogger<OnnxLlmClassifier>.Instance);
    }

    /// <summary>
    ///     Thin <see cref="IClassifierGenerator" /> that increments a
    ///     <see cref="FakeClassifier" />'s call count and returns JSON that
    ///     parses to its <see cref="FakeClassifier.ReturnCategory" /> and
    ///     <see cref="FakeClassifier.ReturnConfidence" />.
    /// </summary>
    private sealed class BridgeGenerator : IClassifierGenerator
    {
        private readonly FakeClassifier mFake;

        public BridgeGenerator(FakeClassifier fake)
        {
            mFake = fake;
        }

        public string ModelId => mFake.ModelId;

        public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        {
            mFake.IncrementCallCount();
            string json = $$$"""{"category": "{{{mFake.ReturnCategory}}}", "confidence": {{{mFake.ReturnConfidence:F1}}}}""";
            return Task.FromResult(json);
        }
    }

    private static PageRecord NewPage() => new()
        {
            Id = "page-1",
            LibraryId = "lib",
            Version = "v1",
            Url = "https://docs.test/page",
            Title = "Test Page",
            Category = DocCategory.Unclassified,
            RawContent = "content",
            FetchedAt = DateTime.UtcNow,
            ContentHash = "hash"
        };

    private static ClassifierBackendSwitch NewSwitch(FakeClassifier onnxFake,
                                                     FakeClassifier ollamaFake,
                                                     bool ollamaReachable = true)
    {
        var onnx = NewOnnxClassifier(onnxFake);
        var probe = new FakeOllamaProbe { Reachable = ollamaReachable };
        return new ClassifierBackendSwitch(onnx, ollamaFake, probe, NullLogger<ClassifierBackendSwitch>.Instance);
    }

    #endregion

    [Fact]
    public async Task DefaultsToOnnxBackend()
    {
        var onnxFake = new FakeClassifier { ReturnCategory = DocCategory.Overview };
        var ollamaFake = new FakeClassifier { ReturnCategory = DocCategory.HowTo };
        var subject = NewSwitch(onnxFake, ollamaFake);

        var result = await subject.ClassifyAsync(NewPage(), "lib", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.Overview, result.Category);
        Assert.Equal(expected: 1, onnxFake.CallCount);
        Assert.Equal(expected: 0, ollamaFake.CallCount);
        Assert.Equal("onnx", subject.ActiveBackendName);
    }

    [Fact]
    public async Task UseOllamaSwitchesDelegationWhenReachable()
    {
        var onnxFake = new FakeClassifier { ReturnCategory = DocCategory.Overview };
        var ollamaFake = new FakeClassifier { ReturnCategory = DocCategory.HowTo, BackendName = "ollama" };
        var subject = NewSwitch(onnxFake, ollamaFake, ollamaReachable: true);

        await subject.UseOllamaAsync(TestContext.Current.CancellationToken);
        var result = await subject.ClassifyAsync(NewPage(), "lib", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.HowTo, result.Category);
        Assert.Equal(expected: 0, onnxFake.CallCount);
        Assert.Equal(expected: 1, ollamaFake.CallCount);
        Assert.Equal("ollama", subject.ActiveBackendName);
    }

    [Fact]
    public async Task UseOllamaThrowsClearErrorWhenUnreachable()
    {
        var onnxFake = new FakeClassifier { ReturnCategory = DocCategory.Overview };
        var ollamaFake = new FakeClassifier { ReturnCategory = DocCategory.HowTo };
        var subject = NewSwitch(onnxFake, ollamaFake, ollamaReachable: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            subject.UseOllamaAsync(TestContext.Current.CancellationToken)
        );

        Assert.Contains("Ollama is not reachable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ollama.com", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("onnx", subject.ActiveBackendName);

        // Confirm delegation still goes to ONNX after the failed switch attempt.
        var result = await subject.ClassifyAsync(NewPage(), "lib", TestContext.Current.CancellationToken);
        Assert.Equal(DocCategory.Overview, result.Category);
        Assert.Equal(expected: 0, ollamaFake.CallCount);
    }

    [Fact]
    public async Task UseOnnxSwitchesBack()
    {
        var onnxFake = new FakeClassifier { ReturnCategory = DocCategory.Overview };
        var ollamaFake = new FakeClassifier { ReturnCategory = DocCategory.HowTo, BackendName = "ollama" };
        var subject = NewSwitch(onnxFake, ollamaFake, ollamaReachable: true);

        await subject.UseOllamaAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ollama", subject.ActiveBackendName);

        subject.UseOnnx();
        Assert.Equal("onnx", subject.ActiveBackendName);

        var result = await subject.ClassifyAsync(NewPage(), "lib", TestContext.Current.CancellationToken);

        Assert.Equal(DocCategory.Overview, result.Category);
        Assert.Equal(expected: 1, onnxFake.CallCount);
        Assert.Equal(expected: 0, ollamaFake.CallCount);
    }

    [Fact]
    public void BackendIdentityReflectsActiveBackend()
    {
        var onnxFakeClassifier = new FakeClassifier
            {
                ReturnCategory = DocCategory.Overview,
                ModelId = "phi-3-mini-4k-instruct-directml"
            };
        var onnx = NewOnnxClassifier(onnxFakeClassifier);
        var ollamaFake = new FakeClassifier { ReturnCategory = DocCategory.HowTo };
        var probe = new FakeOllamaProbe { Reachable = true };
        var sut = new ClassifierBackendSwitch(onnx, ollamaFake, probe, NullLogger<ClassifierBackendSwitch>.Instance);

        Assert.Equal("onnx", sut.BackendName);
        Assert.Equal("phi-3-mini-4k-instruct-directml", sut.ModelId);
        Assert.Contains("phi-3-mini-4k-instruct-directml", sut.GetCurrentVersion());
    }

    [Fact]
    public async Task BackendIdentityFollowsRuntimeSwap()
    {
        var onnxFakeClassifier = new FakeClassifier
            {
                ReturnCategory = DocCategory.Overview,
                ModelId = "phi-3-mini-4k-instruct-directml"
            };
        var onnx = NewOnnxClassifier(onnxFakeClassifier);
        var ollamaFake = new FakeClassifier
            {
                ReturnCategory = DocCategory.HowTo,
                BackendName = "ollama",
                ModelId = "phi4-mini"
            };
        var probe = new FakeOllamaProbe { Reachable = true };
        var sut = new ClassifierBackendSwitch(onnx, ollamaFake, probe, NullLogger<ClassifierBackendSwitch>.Instance);

        Assert.Equal("onnx", sut.BackendName);

        await sut.UseOllamaAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ollama", sut.BackendName);
        Assert.Equal("phi4-mini", sut.ModelId);
        Assert.Contains("phi4-mini", sut.GetCurrentVersion());

        sut.UseOnnx();

        Assert.Equal("onnx", sut.BackendName);
    }
}
