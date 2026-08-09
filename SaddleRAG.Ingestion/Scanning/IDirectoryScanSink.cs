// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#pragma warning disable STR0010 // Interface methods cannot validate parameters

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Consumes documents acquired by the shared directory scan engine.</summary>
public interface IDirectoryScanSink
{
    Task AcceptAsync(DirectoryAcquiredDocument document, CancellationToken ct = default);
}
