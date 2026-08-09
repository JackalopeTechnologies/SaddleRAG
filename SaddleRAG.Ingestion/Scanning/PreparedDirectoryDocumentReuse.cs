// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>An unchanged document whose prior extraction can be accepted after budget reservation.</summary>
internal sealed record PreparedDirectoryDocumentReuse(DirectoryStableDocument Document,
                                                       PriorDirectoryDocument Prior)
{
    internal int SectionCount => Prior.Pages.Count;
}
