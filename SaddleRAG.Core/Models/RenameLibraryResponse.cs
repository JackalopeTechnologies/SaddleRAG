// RenameLibraryResponse.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Core.Models;

/// <summary>
///     Response from ILibraryRepository.RenameAsync. Counts is null
///     when Outcome is Collision or NotFound.
/// </summary>
public sealed record RenameLibraryResponse(RenameLibraryOutcome Outcome,
                                           RenameLibraryResult? Counts);
