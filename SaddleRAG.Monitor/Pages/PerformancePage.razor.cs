// PerformancePage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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

    private System.Threading.Timer? mTimer;

    /// <inheritdoc />
    protected override Task OnInitializedAsync()
    {
        Refresh();
        mTimer = new System.Threading.Timer(_ => InvokeAsync(() =>
            {
                Refresh();
                StateHasChanged();
            }),
                                            state: null,
                                            dueTime: RefreshIntervalMs,
                                            period: RefreshIntervalMs);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        mTimer?.Dispose();
    }

    private void Refresh()
    {
        ArgumentNullException.ThrowIfNull(Metrics);
        Snapshot = Metrics.Snapshot();
    }

    private const int RefreshIntervalMs = 1000;
}
