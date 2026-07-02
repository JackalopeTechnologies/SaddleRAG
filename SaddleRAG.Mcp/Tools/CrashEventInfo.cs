// CrashEventInfo.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Mcp.Tools;

/// <summary>
///     One crash-related Windows event: when it happened and its rendered
///     message (which for .NET Runtime 1026 events contains the managed
///     stack of the unhandled exception).
/// </summary>
public sealed record CrashEventInfo(DateTime TimeUtc, string Message);
