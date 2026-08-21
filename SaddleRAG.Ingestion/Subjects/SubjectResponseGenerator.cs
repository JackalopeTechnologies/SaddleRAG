// SubjectResponseGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Classification;

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Generates one validated structured subject response with a single bounded retry.</summary>
internal static class SubjectResponseGenerator
{
    public static Task<T> GenerateAsync<T>(IClassifierTextGenerator generator,
                                           string prompt,
                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrEmpty(prompt);

        Task<T> result = GenerateValidatedAsync<T, T>(generator,
                                                       prompt,
                                                       static response => response,
                                                       ct);
        return result;
    }

    public static async Task<TResult> GenerateValidatedAsync<TResponse, TResult>(
        IClassifierTextGenerator generator,
        string prompt,
        Func<TResponse, TResult> validate,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        ArgumentNullException.ThrowIfNull(validate);

        string response = await generator.GenerateAsync(prompt, ct);
        TResult result;
        try
        {
            TResponse parsed = SubjectJson.Deserialize<TResponse>(response);
            result = validate(parsed);
        }
        catch(InvalidDataException)
        {
            string retryPrompt = string.Concat(RetryInstruction, prompt);
            response = await generator.GenerateAsync(retryPrompt, ct);
            TResponse parsed = DeserializeCapturingRaw<TResponse>(response);
            result = validate(parsed);
        }

        return result;
    }

    /// <summary>
    ///     Parses the terminal reply, converting an unparseable result into a
    ///     <see cref="SubjectClassificationException" /> that carries the raw text.
    ///     A semantic validation failure downstream stays a sanitized
    ///     <see cref="InvalidDataException" />.
    /// </summary>
    private static TResponse DeserializeCapturingRaw<TResponse>(string response)
    {
        TResponse parsed;
        try
        {
            parsed = SubjectJson.Deserialize<TResponse>(response);
        }
        catch(InvalidDataException ex)
        {
            throw new SubjectClassificationException(response, ex);
        }

        return parsed;
    }

    internal const string RetryInstruction =
        """
        Your previous response could not be accepted. Retry the complete task below.
        Return exactly one complete JSON object that matches the requested schema.
        Follow every validation rule and use only identifiers explicitly allowed by the task.
        Do not use Markdown fences, XML wrappers, comments, or explanatory prose.

        """;
}
