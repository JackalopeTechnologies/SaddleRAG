// DirectoryEntryIdentity.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Stable operating-system identity for one opened filesystem entry.</summary>
public readonly record struct DirectoryEntryIdentity(ulong VolumeId,
                                                     ulong FileIdHigh,
                                                     ulong FileIdLow);
