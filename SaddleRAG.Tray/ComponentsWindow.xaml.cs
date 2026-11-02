// ComponentsWindow.xaml.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tray;

/// <summary>
///     Shows each component the tray can control: its state, where it runs, and Start, Stop and
///     Restart buttons, plus Restart all. It checks the components when it opens and then every
///     <see cref="RefreshIntervalSeconds" /> seconds, and only while it is open: the timer starts when
///     the window loads and stops when it closes. The view model outlives the window, so an action
///     still running when the window closes is still shown when it is opened again.
/// </summary>
public partial class ComponentsWindow : Window
{
    public ComponentsWindow(ComponentsViewModel viewModel, ILogger<ComponentsWindow>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        mViewModel = viewModel;
        mLogger = logger;
        DataContext = viewModel;
        mTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds) };
        mTimer.Tick += OnTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>How often the components are checked while the window is open.</summary>
    public const int RefreshIntervalSeconds = 5;

    private const string RefreshFailedLogMessage = "Components window refresh failed";

    private readonly ComponentsViewModel mViewModel;
    private readonly ILogger<ComponentsWindow>? mLogger;
    private readonly DispatcherTimer mTimer;
    private readonly CancellationTokenSource mClosed = new();
    private bool mRefreshing;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        mTimer.Start();
        _ = RefreshAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        mTimer.Stop();
        mClosed.Cancel();
    }

    private void OnTick(object? sender, EventArgs e) => _ = RefreshAsync();

    // A check can take a few seconds when a component is not answering; a tick that finds the
    // previous check still running leaves it to finish rather than queueing another.
    private async Task RefreshAsync()
    {
        if (!mRefreshing)
        {
            mRefreshing = true;
            try
            {
                await mViewModel.RefreshAsync(mClosed.Token);
            }
            catch(OperationCanceledException) when (mClosed.IsCancellationRequested)
            {
                // The window closed while the check was running.
            }
            catch(Exception ex)
            {
                // The view model absorbs a failed check per row, so this is unexpected; a timer
                // tick must never take down the tray.
                mLogger?.LogWarning(ex, RefreshFailedLogMessage);
            }
            finally
            {
                mRefreshing = false;
            }
        }
    }
}
