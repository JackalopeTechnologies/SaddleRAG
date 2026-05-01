// StoplistMatch.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Ingestion.Symbols;

/// <summary>
///     Result of a profile-aware stoplist check.
/// </summary>
public enum StoplistMatch
{
    /// <summary>Token was not in any stoplist.</summary>
    None,

    /// <summary>Token matched the universal Stoplist.</summary>
    Global,

    /// <summary>Token matched LibraryProfile.Stoplist.</summary>
    Library
}
