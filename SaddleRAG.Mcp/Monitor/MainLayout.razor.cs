// MainLayout.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.AspNetCore.Components;
using SaddleRAG.Core;
using SaddleRAG.Core.Interfaces;

#endregion

namespace SaddleRAG.Mcp.Monitor;

public abstract class MainLayoutBase : LayoutComponentBase, IDisposable
{
    [Inject]
    private IServerLogReader? LogReader { get; set; }

    [Inject]
    private IServerLogAcknowledgement? Acknowledgement { get; set; }

    protected bool DrawerOpen { get; set; } = true;

    /// <summary>Running build, short form, shown beside the product name.</summary>
    protected static string ServerVersion => SaddleRagVersion.Display;

    /// <summary>Running build including <c>+sha</c> build metadata, for support and bug reports.</summary>
    protected static string ServerVersionDetail => SaddleRagVersion.Informational;

    /// <summary>
    ///     Error+Fatal entries the operator has not read yet, shown as the Logs
    ///     nav badge: entries from the last hour that are newer than the last
    ///     time the Logs page was viewed, so reading the log clears the badge.
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
        ArgumentNullException.ThrowIfNull(Acknowledgement);
        try
        {
            RecentErrorCount = LogReader.CountRecentErrors(UnreadWindow());
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            // Deliberately swallowed: badge is best-effort and keeps its last
            // value; the Logs page surfaces read failures (issue #143).
        }
    }

    /// <summary>
    ///     The trailing span the badge still owes the operator: the rolling hour,
    ///     shortened to whatever has arrived since the Logs page was last read.
    ///     A non-positive span means everything on file has been seen, and the
    ///     reader's cutoff then lands in the future and counts nothing.
    /// </summary>
    private TimeSpan UnreadWindow()
    {
        ArgumentNullException.ThrowIfNull(Acknowledgement);
        TimeSpan result = smErrorWindow;
        if (Acknowledgement.AcknowledgedThrough is DateTimeOffset acknowledged)
        {
            TimeSpan sinceAcknowledged = DateTimeOffset.UtcNow - acknowledged;
            if (sinceAcknowledged < result)
                result = sinceAcknowledged;
        }

        return result;
    }

    private static readonly TimeSpan smErrorWindow = TimeSpan.FromHours(1);

    private const int BadgeRefreshIntervalMs = 30_000;
}
