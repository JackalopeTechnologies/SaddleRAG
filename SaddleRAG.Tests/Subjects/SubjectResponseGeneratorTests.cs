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
    public async Task TwoInvalidResponsesSurfaceABoundedRawPreviewAndCarryTheFullReply()
    {
        const string marker = "PRIVATE-SYNTHETIC-DOCUMENT-CONTENT";
        string invalidResponse = string.Concat("{\"value\":\"",
                                               marker,
                                               new string('x',
                                                          SubjectClassificationLimits.MaxRawResponsePreviewCharacters));
        var generator = new ScriptedSubjectGenerator(invalidResponse, invalidResponse);

        SubjectClassificationException failure =
            await Assert.ThrowsAsync<SubjectClassificationException>(() =>
                SubjectResponseGenerator.GenerateAsync<Dictionary<string, JsonElement>>(
                    generator,
                    "Classify the synthetic document.",
                    TestContext.Current.CancellationToken));

        Assert.Equal(2, generator.Prompts.Count);
        // The complete reply is captured for local diagnosis.
        Assert.Equal(invalidResponse, failure.RawResponse);
        // A preview of the reply is now surfaced (the deliberate reversal of the old guarantee) ...
        Assert.Contains(marker, failure.Message, StringComparison.Ordinal);
        // ... but only a bounded preview: the full reply is never rendered into the message.
        Assert.DoesNotContain(invalidResponse, failure.Message, StringComparison.Ordinal);
    }

    private static string ValidateValue(JsonElement value)
    {
        string result = value.GetString() ?? string.Empty;
        if (!string.Equals(result, "accepted", StringComparison.Ordinal))
            throw new InvalidDataException("The synthetic value is not allowed.");
        return result;
    }
}
