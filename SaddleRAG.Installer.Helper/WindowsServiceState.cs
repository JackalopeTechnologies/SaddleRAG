// WindowsServiceState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Installer.Helper;

/// <summary>Service states relevant to the bounded startup loop.</summary>
public enum WindowsServiceState
{
    Unknown,
    Stopped,
    StartPending,
    StopPending,
    Running
}
