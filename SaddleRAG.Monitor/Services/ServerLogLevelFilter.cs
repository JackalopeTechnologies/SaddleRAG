// ServerLogLevelFilter.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Monitor.Services;

/// <summary>
///     Level threshold options offered by the Logs page.
/// </summary>
public enum ServerLogLevelFilter
{
    All,
    WarningsPlus,
    ErrorsOnly
}
