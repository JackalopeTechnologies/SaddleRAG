// OnnxClassifierGeneratorContractTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text;
using SaddleRAG.Core.Models;
using SaddleRAG.Ingestion.Classification;

namespace SaddleRAG.Tests.Classification;

/// <summary>Locks down model-independent ONNX prompt framing and budgeting.</summary>
public sealed class OnnxClassifierGeneratorContractTests
{
    [Fact]
    public void BuildChatPromptMatchesInstalledPhi3Template()
    {
        string result = OnnxClassifierGenerator.BuildChatPrompt("Classify this document.");

        const string expected =
            "<|system|>\nYou are a documentation classifier. Respond with ONLY the requested JSON object.<|end|>\n<|user|>\nClassify this document.<|end|>\n<|assistant|>\n";
        Assert.Equal(expected, result);
        Assert.Equal("<|end|>", ClassifierModelEntry.DefaultStop);
    }

    [Fact]
    public void PromptBudgetReservesEveryConfiguredOutputToken()
    {
        var entry = new ClassifierModelEntry
                        {
                            MaxContextLength = 4096,
                            MaxOutputTokens = 512
                        };

        int result = OnnxClassifierGenerator.GetPromptTokenBudget(entry);

        Assert.Equal(3584, result);
        Assert.Equal(entry.MaxContextLength, result + entry.MaxOutputTokens);
    }

    [Fact]
    public void ExactBoundaryPromptIsNotTruncated()
    {
        const string prompt = "Instruction prefix and evidence";
        int budget = CountChatTokens(prompt);

        string result = OnnxClassifierGenerator.FitPromptToTokenBudget(prompt,
                                                                        budget,
                                                                        CountChatTokens,
                                                                        TakePromptTokens);

        Assert.Equal(prompt, result);
    }

    [Fact]
    public void OversizedPromptIsTruncatedDeterministicallyWithIntactUnicodeAndNotice()
    {
        const string instructionPrefix = "Keep this complete instruction prefix. ";
        string prompt = string.Concat(instructionPrefix,
                                      string.Concat(Enumerable.Repeat("😀evidence ", 40)));
        int budget = CountChatTokens(string.Concat(instructionPrefix,
                                                   "😀evidence ",
                                                   OnnxClassifierGenerator.PromptTruncationNotice));

        string first = OnnxClassifierGenerator.FitPromptToTokenBudget(prompt,
                                                                       budget,
                                                                       CountChatTokens,
                                                                       TakePromptTokens);
        string second = OnnxClassifierGenerator.FitPromptToTokenBudget(prompt,
                                                                        budget,
                                                                        CountChatTokens,
                                                                        TakePromptTokens);

        Assert.Equal(first, second);
        Assert.StartsWith(instructionPrefix, first, StringComparison.Ordinal);
        Assert.EndsWith(OnnxClassifierGenerator.PromptTruncationNotice,
                        first,
                        StringComparison.Ordinal);
        Assert.Equal(budget, CountChatTokens(first));
        Assert.DoesNotContain('\uFFFD', first);
    }

    [Theory]
    [InlineData(" {\"value\":\"accepted\"}\n\n- Note: trailing prose", " {\"value\":\"accepted\"}")]
    [InlineData("{\"first\":1}{\"second\":2}", "{\"first\":1}")]
    [InlineData("{\"value\":\"brace } and escaped quote \\\" stay inside\",\"nested\":{\"ok\":true}} trailing",
                "{\"value\":\"brace } and escaped quote \\\" stay inside\",\"nested\":{\"ok\":true}}")]
    public void CompleteRootJsonObjectStopsBeforeFurtherModelOutput(string response, string expected)
    {
        int end = OnnxClassifierGenerator.FindCompleteRootJsonObjectEnd(response);

        Assert.True(end > 0);
        Assert.Equal(expected, response[..end]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"value\":")]
    [InlineData("Here is {\"value\":1}")]
    [InlineData("[{\"value\":1}]")]
    public void IncompleteOrNonObjectResponseDoesNotTriggerEarlyCompletion(string response)
    {
        int result = OnnxClassifierGenerator.FindCompleteRootJsonObjectEnd(response);

        Assert.Equal(-1, result);
    }

    private static int CountChatTokens(string prompt)
    {
        int result = prompt.EnumerateRunes().Count() + ChatFramingTokens;
        return result;
    }

    private static string TakePromptTokens(string prompt, int maxTokens)
    {
        string result = string.Concat(prompt.EnumerateRunes()
                                            .Take(maxTokens)
                                            .Select(rune => rune.ToString()));
        return result;
    }

    private const int ChatFramingTokens = 20;
}
