// SubjectJsonTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json;
using SaddleRAG.Ingestion.Subjects;

namespace SaddleRAG.Tests.Subjects;

/// <summary>Verifies the strict, known framing accepted from subject models.</summary>
public sealed class SubjectJsonTests
{
    [Theory]
    [InlineData("{\"value\":\"accepted\"}")]
    [InlineData("```json\n{\"value\":\"accepted\"}\n```")]
    [InlineData("```\n{\"value\":\"accepted\"}\n```")]
    [InlineData("<json>\n{\"value\":\"accepted\"}\n</json>")]
    public void DeserializeAcceptsOneObjectInKnownCompleteFraming(string response)
    {
        Dictionary<string, JsonElement> result =
            SubjectJson.Deserialize<Dictionary<string, JsonElement>>(response);

        Assert.Equal("accepted", result["value"].GetString());
    }

    [Theory]
    [InlineData("Here is the result: {\"value\":\"rejected\"}")]
    [InlineData("{\"value\":\"rejected\"} trailing explanation")]
    [InlineData("{\"first\":1}{\"second\":2}")]
    [InlineData("```json\n{\"value\":\"rejected\"}\n``` trailing explanation")]
    [InlineData("<json>{\"value\":\"rejected\"}</json> trailing explanation")]
    [InlineData("[1,2,3]")]
    public void DeserializeRejectsProseMultipleValuesAndNonObjectRoots(string response)
    {
        Assert.Throws<InvalidDataException>(() =>
            SubjectJson.Deserialize<Dictionary<string, JsonElement>>(response));
    }

    [Fact]
    public void InvalidJsonDiagnosticReportsPositionWithoutEchoingResponse()
    {
        const string sensitiveMarker = "PRIVATE-DOCUMENT-CONTENT";
        string response = $"{{\"{sensitiveMarker}\":\"not-an-integer\"}}";

        var exception = Assert.Throws<InvalidDataException>(() =>
            SubjectJson.Deserialize<Dictionary<string, int>>(response));

        Assert.Contains("characters=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("line=", exception.Message, StringComparison.Ordinal);
        Assert.Contains("byte=", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMarker, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMarker, exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void EmptyResponseIsReportedAsSanitizedInvalidData()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            SubjectJson.Deserialize<Dictionary<string, JsonElement>>(string.Empty));

        Assert.Equal("The subject classifier returned an empty response (characters=0).",
                     exception.Message);
        Assert.Null(exception.InnerException);
    }
}
