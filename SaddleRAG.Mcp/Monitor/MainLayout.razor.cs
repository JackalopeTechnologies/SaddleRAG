// MainLayout.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.AspNetCore.Components;
using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Mcp.Monitor;

public abstract class MainLayoutBase : LayoutComponentBase, IDisposable
{
    [Inject]
    private IServerLogReader? LogReader { get; set; }

    protected bool DrawerOpen { get; set; } = true;

    /// <summary>
    ///     Error+Fatal entries in the last hour, shown as the Logs nav badge.
    ///     Refreshed every 30 s; a failed poll keeps the previous value by
    ///     design — the badge is best-effort, the Logs page is the surface
    ///     that reports read failures (issue #143).
    /// </summary>
    protected int RecentErrorCount { get; private set; }

    private Timer? mTimer;

    /// <inheritdoc />
    public void Dispose()
    {
        mTimer?.Dispose();
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync()
    {
        RefreshErrorCount();
        mTimer = new Timer(_ => InvokeAsync(() =>
                                            {
                                                RefreshErrorCount();
                                                StateHasChanged();
                                            }
                                           ),
                           state: null,
                           BadgeRefreshIntervalMs,
                           BadgeRefreshIntervalMs
                          );
        return Task.CompletedTask;
    }

    protected void ToggleDrawer()
    {
        DrawerOpen = !DrawerOpen;
    }

    private void RefreshErrorCount()
    {
        ArgumentNullException.ThrowIfNull(LogReader);
        try
        {
            RecentErrorCount = LogReader.CountRecentErrors(smErrorWindow);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately swallowed: badge is best-effort and keeps its last
            // value; the Logs page surfaces read failures (issue #143).
        }
    }

    private static readonly TimeSpan smErrorWindow = TimeSpan.FromHours(1);

    private const int BadgeRefreshIntervalMs = 30_000;
}
