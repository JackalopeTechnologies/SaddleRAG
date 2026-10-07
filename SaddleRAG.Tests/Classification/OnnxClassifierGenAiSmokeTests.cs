// OnnxClassifierGenAiSmokeTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Subjects;
using SaddleRAG.Tests.Subjects;

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
        var classifier = new OnnxLlmClassifier(generator,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OnnxLlmClassifier>.Instance);
        var subjectClassifier = new SubjectClassifier(classifier, new FixedSubjectTimeProvider());
        var assignments = new InMemorySubjectAssignmentRepository();
        SubjectAssignmentRecord result = await subjectClassifier.ClassifyAsync(assignments,
            SubjectTestData.Descriptor(), SubjectTestData.Catalog(), "smoke-test", "native-generation",
            TestContext.Current.CancellationToken);

        Assert.Equal("subject-hydraulics", result.Primary.SubjectId);
        Assert.DoesNotContain(result.Secondary, selection => selection.SubjectId == "subject-electrical");
        Assert.NotEmpty(result.Primary.Evidence);
        Assert.Same(result, Assert.Single(assignments.Persisted));
    }

    private const string GenAiConfigFileName = "genai_config.json";
    private const string MissingModelMessage =
        "The Phi-3 GenAI classifier model is not staged for this execution provider; skipping the GenAI native smoke test.";
}
