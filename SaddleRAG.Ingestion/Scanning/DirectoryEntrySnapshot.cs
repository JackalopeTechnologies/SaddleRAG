// DirectoryEntrySnapshot.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Metadata captured for one filesystem entry.</summary>
public sealed record DirectoryEntrySnapshot(string FullPath,
                                            FileAttributes Attributes,
                                            long ByteLength,
                                            DateTime LastWriteTimeUtc);
