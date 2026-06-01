// McpServiceState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

public enum McpServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    Running,
    Transitioning
}
