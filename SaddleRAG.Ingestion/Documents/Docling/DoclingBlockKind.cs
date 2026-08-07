// DoclingBlockKind.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>Stable document block type mapped from lossless Docling JSON.</summary>
public enum DoclingBlockKind
{
    Text,
    Heading,
    Table
}
