// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Named stages of a manual directory import.</summary>
public static class DirectoryScanPhases
{
    public const string Extracting = "Extracting documents";
    public const string Labeling = "Labeling documents";
    public const string PreparingSearch = "Preparing documents for search";
    public const string BuildingIndex = "Building search index";
    public const string Publishing = "Publishing library";
}
