// OnnxClassifierControlledOutputDiagnosticTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json;
using SaddleRAG.Core.Enums;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;
using SaddleRAG.Ingestion.Embedding;
using SaddleRAG.Ingestion.Subjects;
using SaddleRAG.Tests.Subjects;

namespace SaddleRAG.Tests.Classification;

/// <summary>
///     Explicitly gated capture of local-model output for repository-owned,
///     synthetic subject prompts. Ordinary and production runs cannot enable
///     output capture through this test-only path.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OnnxClassifierControlledOutputDiagnosticTests
{
    [Fact]
    public async Task SyntheticSubjectPromptsCanBeCapturedToNewTemporaryFile()
    {
        bool enabled = string.Equals(Environment.GetEnvironmentVariable(OptInVariable),
                                     EnabledValue,
                                     StringComparison.Ordinal);
        Assert.SkipUnless(enabled, OptInSkipReason);

        string outputFileName = Environment.GetEnvironmentVariable(OutputFileVariable) ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(outputFileName), MissingOutputFileMessage);
        Assert.Equal(Path.GetFileName(outputFileName), outputFileName);
        string outputPath = Path.Combine(Path.GetTempPath(), outputFileName);
        Assert.False(File.Exists(outputPath), ExistingOutputFileMessage);

        var settings = new OnnxSettings();
        ClassifierModelEntry entry = ClassifierEntryResolver.Resolve(settings,
                                                                     OnnxExecutionProvider.Cpu);
        string modelDirectory = Path.Combine(settings.ModelsDir, entry.Name);
        Assert.SkipUnless(File.Exists(Path.Combine(modelDirectory, GenAiConfigFileName)),
                          MissingModelMessage);

        using var generator = new OnnxClassifierGenerator(modelDirectory, entry);
        SubjectDescriptor descriptor = SubjectTestData.Descriptor();
        string catalogPrompt = SubjectCatalogPrompt.Build([], descriptor);
        SubjectConcept existingConcept = new()
                                             {
                                                 Id = "subject-hydraulic-pump-safety",
                                                 Label = descriptor.Title,
                                                 Aliases = ["hydraulic-pump-safety"],
                                                 Description = descriptor.Summary
                                             };
        string reuseCatalogPrompt = SubjectCatalogPrompt.Build([existingConcept], descriptor);
        string assignmentPrompt = SubjectClassificationPrompt.Build(descriptor, SubjectTestData.Catalog());
        string catalogOutput = await generator.GenerateAsync(catalogPrompt,
                                                              TestContext.Current.CancellationToken);
        string reuseCatalogOutput = await generator.GenerateAsync(reuseCatalogPrompt,
                                                                   TestContext.Current.CancellationToken);
        string assignmentOutput = await generator.GenerateAsync(assignmentPrompt,
                                                                 TestContext.Current.CancellationToken);

        await using var output = new FileStream(outputPath,
                                                FileMode.CreateNew,
                                                FileAccess.Write,
                                                FileShare.Read);
        await JsonSerializer.SerializeAsync(output,
                                            new
                                                {
                                                    catalogOutput,
                                                    reuseCatalogOutput,
                                                    assignmentOutput
                                                },
                                            cancellationToken: TestContext.Current.CancellationToken);

        using JsonDocument catalogDocument = JsonDocument.Parse(catalogOutput);
        using JsonDocument reuseCatalogDocument = JsonDocument.Parse(reuseCatalogOutput);
        using JsonDocument assignmentDocument = JsonDocument.Parse(assignmentOutput);
        Assert.Equal(JsonValueKind.Object, catalogDocument.RootElement.ValueKind);
        JsonElement newConcept = Assert.Single(catalogDocument.RootElement
                                                              .GetProperty("concepts")
                                                              .EnumerateArray()
                                                              .ToArray());
        Assert.Equal(JsonValueKind.Null, newConcept.GetProperty("subjectId").ValueKind);

        Assert.Equal(JsonValueKind.Object, reuseCatalogDocument.RootElement.ValueKind);
        JsonElement reusedConcept = Assert.Single(reuseCatalogDocument.RootElement
                                                                      .GetProperty("concepts")
                                                                      .EnumerateArray()
                                                                      .ToArray());
        JsonElement reusedSubjectId = reusedConcept.GetProperty("subjectId");
        Assert.True(reusedSubjectId.ValueKind is JsonValueKind.Null or JsonValueKind.String);
        if (reusedSubjectId.ValueKind == JsonValueKind.String)
            Assert.Equal(existingConcept.Id, reusedSubjectId.GetString());

        Assert.Equal(JsonValueKind.Object, assignmentDocument.RootElement.ValueKind);
        var allowedIds = SubjectTestData.Catalog().Concepts.Select(concept => concept.Id)
                                        .ToHashSet(StringComparer.Ordinal);
        JsonElement primary = assignmentDocument.RootElement.GetProperty("primary");
        string primarySubjectId = Assert.IsType<string>(primary.GetProperty("subjectId").GetString());
        Assert.Contains(primarySubjectId, allowedIds);
        JsonElement[] secondary = assignmentDocument.RootElement.GetProperty("secondary")
                                                    .EnumerateArray()
                                                    .ToArray();
        Assert.All(secondary,
                   selection =>
                   {
                       Assert.Equal(JsonValueKind.Object, selection.ValueKind);
                       string subjectId = Assert.IsType<string>(selection.GetProperty("subjectId").GetString());
                       Assert.Contains(subjectId, allowedIds);
                       Assert.Equal(JsonValueKind.Number, selection.GetProperty("confidence").ValueKind);
                       Assert.Equal(JsonValueKind.Array, selection.GetProperty("evidence").ValueKind);
                   });
    }

    internal const string OptInVariable = "SADDLERAG_RUN_CONTROLLED_CLASSIFIER_DIAGNOSTIC";
    internal const string OutputFileVariable = "SADDLERAG_CONTROLLED_CLASSIFIER_OUTPUT_FILE";
    internal const string EnabledValue = "1";
    private const string OptInSkipReason =
        "Set SADDLERAG_RUN_CONTROLLED_CLASSIFIER_DIAGNOSTIC=1 only for an explicitly approved controlled-output diagnostic.";
    private const string MissingOutputFileMessage =
        "Set SADDLERAG_CONTROLLED_CLASSIFIER_OUTPUT_FILE to a new file name under the process temporary directory.";
    private const string ExistingOutputFileMessage =
        "The controlled classifier diagnostic never overwrites an existing output file.";
    private const string MissingModelMessage = "The installed Phi-3 CPU classifier model is unavailable.";
    private const string GenAiConfigFileName = "genai_config.json";
}
