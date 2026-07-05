// LogsPage.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.AspNetCore.Components;
using MudBlazor;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Monitor.Services;

#endregion

namespace SaddleRAG.Monitor.Pages;

/// <summary>
///     Code-behind for the /monitor/logs page. Polls the
///     <see cref="IServerLogReader" /> singleton every two seconds (when
///     auto-refresh is on) and exposes the filtered entries to the view. A
///     failed poll keeps the last good snapshot and surfaces
///     <see cref="ReadError" /> instead of faulting the circuit.
/// </summary>
public abstract class LogsPageBase : ComponentBase, IDisposable
{
    [Inject]
    private IServerLogReader? LogReader { get; set; }

    /// <summary>
    ///     Most recent successfully read snapshot; null until the first read.
    /// </summary>
    protected ServerLogSnapshot? Snapshot { get; private set; }

    /// <summary>
    ///     Message of the most recent failed poll; null when the last poll succeeded.
    /// </summary>
    protected string? ReadError { get; private set; }

    protected ServerLogLevelFilter LevelFilter { get; set; } = ServerLogLevelFilter.All;

    protected string FilterText { get; set; } = string.Empty;

    protected bool AutoRefresh { get; set; } = true;

    #region MaxEntries property
    private int mMaxEntries = DefaultMaxEntries;
    protected int MaxEntries
    {
        get => mMaxEntries;
        set
        {
            mMaxEntries = value;
            RefreshNow();
        }
    }
    #endregion

    protected IReadOnlyList<ServerLogEntry> FilteredEntries =>
        Snapshot == null ? [] : ServerLogFilter.Apply(Snapshot.Entries, LevelFilter, FilterText);

    private readonly HashSet<ServerLogEntry> mExpanded = new(ReferenceEqualityComparer.Instance);

    private Timer? mTimer;

    /// <inheritdoc />
    public void Dispose()
    {
        mTimer?.Dispose();
    }

    /// <inheritdoc />
    protected override Task OnInitializedAsync()
    {
        RefreshNow();
        mTimer = new Timer(_ => InvokeAsync(() =>
                                            {
                                                if (AutoRefresh)
                                                {
                                                    RefreshNow();
                                                    StateHasChanged();
                                                }
                                            }
                                           ),
                           state: null,
                           RefreshIntervalMs,
                           RefreshIntervalMs
                          );
        return Task.CompletedTask;
    }

    protected bool IsExpanded(ServerLogEntry entry) => mExpanded.Contains(entry);

    protected void ToggleExpanded(ServerLogEntry entry)
    {
        if (!mExpanded.Remove(entry))
            mExpanded.Add(entry);
    }

    protected void RefreshNow()
    {
        ArgumentNullException.ThrowIfNull(LogReader);
        try
        {
            Snapshot = LogReader.Read(MaxEntries);
            ReadError = null;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            ReadError = ex.Message;
        }
    }

    protected static Color LevelColor(ServerLogLevel level) => level switch
                                                               {
                                                                   ServerLogLevel.Fatal => Color.Error,
                                                                   ServerLogLevel.Error => Color.Error,
                                                                   ServerLogLevel.Warning => Color.Warning,
                                                                   ServerLogLevel.Information => Color.Info,
                                                                   _ => Color.Default
                                                               };

    private const int RefreshIntervalMs = 2000;
    private const int DefaultMaxEntries = 250;
}
