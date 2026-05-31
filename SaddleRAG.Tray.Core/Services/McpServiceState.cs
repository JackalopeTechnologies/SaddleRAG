// McpServiceState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

namespace SaddleRAG.Tray.Services;

public enum McpServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    Running,
    Transitioning
}
