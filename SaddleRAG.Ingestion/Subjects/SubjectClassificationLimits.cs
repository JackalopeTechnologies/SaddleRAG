// SubjectClassificationLimits.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Named deterministic bounds for subject prompts and stored evidence.</summary>
public static class SubjectClassificationLimits
{
    public const int MaxTitleCharacters = 512;
    public const int MaxRelativePathCharacters = 1024;
    public const int MaxHeadingCount = 16;
    public const int MaxHeadingCharacters = 256;
    public const int MaxTableOfContentsCharacters = 2048;
    public const int MaxSummaryCharacters = 1200;
    public const int MaxStratifiedSectionCount = 6;
    public const int MaxStratifiedSectionCharacters = 1200;
    public const int MaxEvidenceCount = 6;
    public const int MaxEvidenceCharacters = 512;
    public const int MaxRawResponsePreviewCharacters = 256;
    public const int MaxSecondarySubjects = 3;
    public const float NeedsReviewConfidenceThreshold = 0.65f;
    public const double InferredSubjectBoost = 0.05;
}
