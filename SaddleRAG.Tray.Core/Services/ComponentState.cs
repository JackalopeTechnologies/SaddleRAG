// ComponentState.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

/// <summary>What the tray found when it last checked one component.</summary>
public enum ComponentState
{
    /// <summary>The component is up and answering.</summary>
    Running,

    /// <summary>Nothing is serving: the service is stopped, or nothing listens on the port.</summary>
    Stopped,

    /// <summary>The component should be up but is not answering correctly, or did not answer in time.</summary>
    Unreachable,

    /// <summary>The Windows service that runs the component is not installed.</summary>
    NotInstalled,

    /// <summary>The tray cannot tell: a remote component, a service changing state, or a failed check.</summary>
    Unknown
}