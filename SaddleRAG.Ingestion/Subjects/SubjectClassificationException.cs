// SubjectClassificationException.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>
///     A terminal subject-classification failure raised when the model reply
///     cannot be parsed after the bounded retry. Unlike the sanitized parse
///     diagnostic it wraps, this preserves the raw reply so a scan can log a
///     bounded preview and degrade gracefully instead of discarding the run.
/// </summary>
public sealed class SubjectClassificationException : Exception
{
    public SubjectClassificationException(string rawResponse, InvalidDataException innerException)
        : base(BuildMessage(innerException, rawResponse), innerException)
    {
        RawResponse = rawResponse;
    }

    /// <summary>The complete, unmodified reply the classifier returned.</summary>
    public string RawResponse { get; }

    /// <summary>A whitespace-normalized, length-bounded preview of the raw reply.</summary>
    public string RawResponsePreview =>
        SubjectText.Bounded(RawResponse, SubjectClassificationLimits.MaxRawResponsePreviewCharacters);

    private static string BuildMessage(InvalidDataException innerException, string rawResponse)
    {
        ArgumentNullException.ThrowIfNull(innerException);
        ArgumentNullException.ThrowIfNull(rawResponse);
        string preview = SubjectText.Bounded(rawResponse,
                                            SubjectClassificationLimits.MaxRawResponsePreviewCharacters);
        return string.Concat(innerException.Message, RawPreviewLabel, preview);
    }

    private const string RawPreviewLabel = " Raw reply preview: ";
}
