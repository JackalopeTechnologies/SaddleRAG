// GuidSubjectIdGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Creates random opaque identifiers without exposing display labels.</summary>
public sealed class GuidSubjectIdGenerator : ISubjectIdGenerator
{
    public string CreateId() => $"subject-{Guid.NewGuid():N}";
}
