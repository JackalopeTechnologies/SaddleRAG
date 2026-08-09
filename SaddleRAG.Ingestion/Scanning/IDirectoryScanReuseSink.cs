// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>
///     Optional publishing-sink capability that can consume an unchanged
///     stable file from a prior immutable snapshot before format extraction.
/// </summary>
internal interface IDirectoryScanReuseSink
{
    PreparedDirectoryDocumentReuse? TryPrepareUnchanged(DirectoryStableDocument document);

    Task AcceptPreparedUnchangedAsync(PreparedDirectoryDocumentReuse prepared,
                                      CancellationToken ct = default);
}
