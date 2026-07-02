// WerReportInfo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp.Tools;

/// <summary>One WER AppCrash report folder for the MCP host.</summary>
public sealed record WerReportInfo(string Name, DateTime LastWriteUtc);
