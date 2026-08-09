// ISubjectIdGenerator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Subjects;

/// <summary>Creates SaddleRAG-owned opaque subject identifiers.</summary>
public interface ISubjectIdGenerator
{
    string CreateId();
}
