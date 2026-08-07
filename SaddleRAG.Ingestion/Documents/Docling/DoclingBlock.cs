// DoclingBlock.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>One ordered, provenance-bearing document block.</summary>
public sealed record DoclingBlock(int Order,
                                  DoclingBlockKind Kind,
                                  string Label,
                                  string Text,
                                  int? HeadingLevel,
                                  int? PageNumber,
                                  DoclingBoundingBox? BoundingBox,
                                  IReadOnlyList<DoclingTableCell> TableCells);
