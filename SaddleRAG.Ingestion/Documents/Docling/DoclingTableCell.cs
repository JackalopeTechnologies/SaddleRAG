// DoclingTableCell.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>One table cell mapped from Docling's lossless document form.</summary>
public sealed record DoclingTableCell(int StartRow,
                                      int EndRow,
                                      int StartColumn,
                                      int EndColumn,
                                      string Text,
                                      bool IsColumnHeader,
                                      bool IsRowHeader);
