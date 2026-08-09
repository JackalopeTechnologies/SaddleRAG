// SubjectResponseGeneratorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

public sealed class SubjectResponseGeneratorTests
{
    [Fact]
    public async Task InvalidFirstResponseRetriesWithStrictInstructionAndReturnsSecondObject()
    {
        const string prompt = "Classify the synthetic document.";
        var generator = new ScriptedSubjectGenerator("{not-json}",
                                                     "{\"value\":\"accepted\"}");

        Dictionary<string, JsonElement> result =
            await SubjectResponseGenerator.GenerateAsync<Dictionary<string, JsonElement>>(
                generator,
                prompt,
                TestContext.Current.CancellationToken);

        Assert.Equal("accepted", result["value"].GetString());
        Assert.Equal(2, generator.Prompts.Count);
        Assert.Equal(prompt, generator.Prompts[index: 0]);
        Assert.StartsWith(SubjectResponseGenerator.RetryInstruction,
                          generator.Prompts[index: 1],
                          StringComparison.Ordinal);
        Assert.EndsWith(prompt, generator.Prompts[index: 1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyFirstResponseUsesTheSameBoundedRetry()
    {
        var generator = new ScriptedSubjectGenerator(string.Empty,
                                                     "{\"value\":\"accepted\"}");

        Dictionary<string, JsonElement> result =
            await SubjectResponseGenerator.GenerateAsync<Dictionary<string, JsonElement>>(
                generator,
                "Classify the synthetic document.",
                TestContext.Current.CancellationToken);

        Assert.Equal("accepted", result["value"].GetString());
        Assert.Equal(2, generator.Prompts.Count);
    }

    [Fact]
    public async Task SemanticValidationFailureUsesTheSameBoundedRetry()
    {
        var generator = new ScriptedSubjectGenerator("{\"value\":\"unknown\"}",
                                                     "{\"value\":\"accepted\"}");

        string result = await SubjectResponseGenerator.GenerateValidatedAsync<Dictionary<string, JsonElement>,
            string>(generator,
                    "Classify the synthetic document.",
                    response => ValidateValue(response["value"]),
                    TestContext.Current.CancellationToken);

        Assert.Equal("accepted", result);
        Assert.Equal(2, generator.Prompts.Count);
        Assert.Contains("use only identifiers explicitly allowed",
                        generator.Prompts[index: 1],
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoInvalidResponsesRemainTerminalSanitizedAndBounded()
    {
        const string sensitiveMarker = "PRIVATE-SYNTHETIC-DOCUMENT-CONTENT";
        string invalidResponse = $"{{\"value\":\"{sensitiveMarker}\"";
        var generator = new ScriptedSubjectGenerator(invalidResponse, invalidResponse);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            SubjectResponseGenerator.GenerateAsync<Dictionary<string, JsonElement>>(
                generator,
                "Classify the synthetic document.",
                TestContext.Current.CancellationToken));

        Assert.Equal(2, generator.Prompts.Count);
        Assert.DoesNotContain(sensitiveMarker, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMarker, failure.ToString(), StringComparison.Ordinal);
    }

    private static string ValidateValue(JsonElement value)
    {
        string result = value.GetString() ?? string.Empty;
        if (!string.Equals(result, "accepted", StringComparison.Ordinal))
            throw new InvalidDataException("The synthetic value is not allowed.");
        return result;
    }
}
