// DoclingMappedDocument.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>Lossless response plus the stable subset consumed by SaddleRAG.</summary>
public sealed record DoclingMappedDocument(string FileName,
                                           string MarkdownContent,
                                           string TextContent,
                                           string RawResponseJson,
                                           string RawDocumentJson,
                                           IReadOnlyList<DoclingBlock> Blocks);
