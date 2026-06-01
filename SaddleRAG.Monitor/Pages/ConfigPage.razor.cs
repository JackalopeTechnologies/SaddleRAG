// ConfigPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.AspNetCore.Components;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Code-behind for the /monitor/config page (issue #73). Resolves the
///     <see cref="IMonitorConfigSource" /> from DI and reads the snapshot
///     once per visit. Read-only — no timer-driven refresh because runtime
///     configuration changes are user-driven (MCP tool calls), and on the
///     rare occasion they do change, a browser refresh is the right action.
/// </summary>
public abstract class ConfigPageBase : ComponentBase
{
    [Inject]
    private IMonitorConfigSource? ConfigSource { get; set; }

    /// <summary>
    ///     Snapshot of the current MCP runtime configuration; null until
    ///     <see cref="OnInitializedAsync" /> populates it.
    /// </summary>
    protected MonitorConfigSnapshot? Snapshot { get; private set; }

    /// <inheritdoc />
    protected override Task OnInitializedAsync()
    {
        if (ConfigSource is not null)
            Snapshot = ConfigSource.GetSnapshot();
        return Task.CompletedTask;
    }
}
