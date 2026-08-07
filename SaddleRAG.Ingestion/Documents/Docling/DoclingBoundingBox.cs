// DoclingBoundingBox.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>Bounding box retained in Docling's coordinate system.</summary>
public sealed record DoclingBoundingBox(double Left,
                                        double Top,
                                        double Right,
                                        double Bottom,
                                        string CoordinateOrigin);
