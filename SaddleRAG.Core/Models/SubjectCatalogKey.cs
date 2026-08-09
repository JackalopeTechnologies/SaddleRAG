// SubjectCatalogKey.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models;

/// <summary>Composite lookup key for an immutable subject catalog revision.</summary>
public sealed record SubjectCatalogKey(string LibraryId, string TaxonomyVersion);
