// ServerLogLevel.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Models.Monitor;

/// <summary>
///     Severity of a parsed server log entry. Mirrors Serilog's level ladder
///     without referencing Serilog. Member order is significant: numeric
///     comparisons (<c>&gt;=</c>) are used for threshold filtering.
/// </summary>
public enum ServerLogLevel
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal
}
