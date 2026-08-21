// OnnxClassifierGenAiSmokeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;

namespace SaddleRAG.Tests.Classification;

/// <summary>
///     Loads the Microsoft.ML.OnnxRuntimeGenAI native and runs one real generate
///     against the staged Phi-3 classifier model. Compile and image-build cannot
///     catch an ABI or runtime break a GenAI package bump introduces; this can.
///     Gated on the model being staged for the build's execution provider, so it
///     skips cleanly in CI and on any box without the model. The model variant is
///     resolved to match the linked native (via the compiled-in-provider clamp) so
///     it never triggers the provider-mismatch access violation (issue #135).
/// </summary>
[Trait("Category", "Integration")]
public sealed class OnnxClassifierGenAiSmokeTests
{
    [Fact]
    public async Task StagedPhi3ModelLoadsTheGenAiNativeAndGeneratesOnce()
    {
        var capabilities = new OnnxRuntimeCapabilities();
        var settings = new OnnxSettings();
        OnnxExecutionProvider requested =
            capabilities.CompiledInProviders.Contains(OnnxExecutionProvider.DirectMl)
                ? OnnxExecutionProvider.DirectMl
                : capabilities.CompiledInProviders.Contains(OnnxExecutionProvider.Cuda)
                    ? OnnxExecutionProvider.Cuda
                    : OnnxExecutionProvider.Cpu;
        ClassifierModelEntry entry = ClassifierEntryResolver.Resolve(settings,
                                                                     requested,
                                                                     capabilities.CompiledInProviders);
        string modelDirectory = Path.Combine(settings.ModelsDir, entry.Name);
        Assert.SkipUnless(File.Exists(Path.Combine(modelDirectory, GenAiConfigFileName)), MissingModelMessage);

        using var generator = new OnnxClassifierGenerator(modelDirectory, entry);
        string output = await generator.GenerateAsync(Prompt, TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    private const string Prompt =
        "Classify the following document into one subject. Respond with a single JSON object.";
    private const string GenAiConfigFileName = "genai_config.json";
    private const string MissingModelMessage =
        "The Phi-3 GenAI classifier model is not staged for this execution provider; skipping the GenAI native smoke test.";
}
