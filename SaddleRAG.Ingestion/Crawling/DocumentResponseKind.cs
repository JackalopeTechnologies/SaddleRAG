// DocumentResponseKind.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Crawling;

/// <summary>Supported interpretations of an acquired web response.</summary>
public enum DocumentResponseKind
{
    Other,
    Html,
    Pdf,
    Docx
}
