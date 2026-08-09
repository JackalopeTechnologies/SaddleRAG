// ClassifierPromptEvidence.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Classification;

/// <summary>
///     Marks structured classifier evidence so context-budget compaction can
///     preserve the prompt instructions and a complete JSON value.
/// </summary>
internal static class ClassifierPromptEvidence
{
    internal static string Compose(string instructions, string evidence)
    {
        ArgumentException.ThrowIfNullOrEmpty(instructions);
        ArgumentException.ThrowIfNullOrEmpty(evidence);
        string result = string.Concat(instructions, StartMarker, evidence, EndMarker);
        return result;
    }

    internal static bool TrySplit(string prompt,
                                  out string prefix,
                                  out string evidence,
                                  out string suffix)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        int start = prompt.IndexOf(StartMarker, StringComparison.Ordinal);
        int evidenceStart = start < 0 ? -1 : start + StartMarker.Length;
        int end = evidenceStart < 0
                      ? -1
                      : prompt.IndexOf(EndMarker, evidenceStart, StringComparison.Ordinal);
        bool result = start >= 0 && end >= evidenceStart;
        prefix = result ? prompt[..evidenceStart] : string.Empty;
        evidence = result ? prompt[evidenceStart..end] : string.Empty;
        suffix = result ? prompt[end..] : string.Empty;
        return result;
    }

    private const string StartMarker = "\n--- BEGIN SADDLERAG STRUCTURED EVIDENCE ---\n";
    private const string EndMarker = "\n--- END SADDLERAG STRUCTURED EVIDENCE ---";
}
