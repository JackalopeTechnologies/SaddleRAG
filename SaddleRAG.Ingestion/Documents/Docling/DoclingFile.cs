// DoclingFile.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>Immutable file submitted to the Docling boundary.</summary>
public sealed record DoclingFile(string FileName,
                                 string MediaType,
                                 ReadOnlyMemory<byte> Content);
