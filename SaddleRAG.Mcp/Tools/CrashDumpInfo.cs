// CrashDumpInfo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp.Tools;

/// <summary>One crash dump on disk (issue #136 capture output).</summary>
public sealed record CrashDumpInfo(string FileName, long SizeBytes, DateTime LastWriteUtc);
