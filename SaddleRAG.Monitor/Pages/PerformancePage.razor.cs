// PerformancePage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using Microsoft.AspNetCore.Components;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;

#endregion

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Code-behind for the /monitor/performance page. Polls the
///     <see cref="IQueryMetrics" /> singleton once per second and exposes the
///     latest <see cref="QueryMetricsSnapshot" /> for the Razor view.
/// </summary>
public abstract class PerformancePageBase : ComponentBase, IDisposable
{
    [Inject]
    private IQueryMetrics? Metrics { get; set; }

    /// <summary>
    ///     Most recent snapshot fetched from the recorder; null until the first refresh.
    /// </summary>
    protected QueryMetricsSnapshot? Snapshot { get; private set; }

    private Timer? mTimer;

    /// <inheritdoc />
    public void Dispose()
    {
        mTimer?.Dispose();
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync()
    {
        Refresh();
        mTimer = new Timer(_ => InvokeAsync(() =>
                                            {
                                                Refresh();
                                                StateHasChanged();
                                            }
                                           ),
                           state: null,
                           RefreshIntervalMs,
                           RefreshIntervalMs
                          );
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        ArgumentNullException.ThrowIfNull(Metrics);
        Snapshot = Metrics.Snapshot();
    }

    private const int RefreshIntervalMs = 1000;
}
