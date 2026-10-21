// HealthPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.AspNetCore.Components;
using SaddleRAG.Core;
using SaddleRAG.Core.Models;

namespace SaddleRAG.Mcp.Monitor;

/// <summary>Displays cached observations without querying any dependency.</summary>
public abstract class HealthPageBase : ComponentBase, IDisposable
{
    [Inject] protected DependencyHealthState? Health { get; set; }
    [Inject] protected McpWarmupState? Warmup { get; set; }
    protected IReadOnlyList<DependencyHealth> Observations => Health?.Snapshot() ?? [];
    protected string Overall => Warmup is not null && Health is not null
        ? ServiceHealthResponseFactory.OverallStatus(Warmup, Health) : string.Empty;
    private Timer? mTimer;

    protected override void OnInitialized()
    {
        mTimer = new Timer(_ => _ = InvokeAsync(StateHasChanged), null,
                          TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Dispose() => mTimer?.Dispose();
}
