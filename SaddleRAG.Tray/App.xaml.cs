// App.xaml.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using SaddleRAG.ClientIntegration;
using SaddleRAG.Tray.Core.Logging;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tray;

public partial class App : Application
{
    private const string DashboardUrl = "http://localhost:6100";
    private const string BalloonTitle = "SaddleRAG";

    private const string TrayIconResourceKey = "TrayIcon";
    private const string StartItemName = "StartItem";
    private const string StopItemName = "StopItem";
    private const string InstallMenuName = "InstallMenu";
    private const string UninstallMenuName = "UninstallMenu";

    private const string AllItemHeader = "All";
    private const string AllKey = "all";

    private const string StartingMessage = "Starting MCP service";
    private const string StoppingMessage = "Stopping MCP service";
    private const string OpeningDashboardMessage = "Opening dashboard";
    private const string InstallFailedPrefix = "Install failed: ";
    private const string UninstallFailedPrefix = "Uninstall failed: ";
    private const string StatusFailedPrefix = "Status failed: ";
    private const string StatusFailedLogMessage = "Client-integration status query failed";
    private const string StatusTitle = "SaddleRAG — client status";
    private const string ActionFailedSuffix = " failed: ";
    private const string StartupFailedPrefix = "SaddleRAG tray failed to start: ";
    private const string StartupFailedLogMessage = "Tray failed to start";
    private const string StartedLogMessage = "SaddleRAG tray started";
    private const string InstallLogMessage = "Install into {Client}: {Summary}";
    private const string UninstallLogMessage = "Uninstall from {Client}: {Summary}";
    private const string InstallFailedLogMessage = "Install into {Client} failed";
    private const string UninstallFailedLogMessage = "Uninstall from {Client} failed";
    private const string ActionFailedLogMessage = "{Action} failed";
    private const string IconRenderFailedLogMessage = "Tray icon render failed";
    private const string AutoRegisterLogMessage = "Auto-registered detected clients on startup: {Summary}";
    private const string AutoRegisterPartialFailLogMessage = "Auto-register: one or more clients failed on startup: {Summary}";

    // Mirrors ClientResultFormatter.ErrPrefix — a per-client registration failure renders with
    // this status token in the summary (OK/SKIP do not), so a real failure can be raised to Warning.
    private const string RegistrationFailedMarker = "ERR";
    private const string AutoRegisterFailedLogMessage = "Auto-register of detected clients on startup failed";

    private const string LogDirName = "SaddleRAG";
    private const string LogFileName = "tray.log";

    private TaskbarIcon? mTrayIcon;
    private McpServiceMenuModel? mMenuModel;
    private ILogger<App>? mLog;
    private ILoggerFactory? mLoggerFactory;
    private readonly TrayClientService mClientService = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Startup is the one path that runs unattended right after install. Guard it so a
        // failure produces a visible message (the tray icon may not exist yet, so use a
        // MessageBox, not a balloon) and a log entry, rather than a silent crash that the
        // installer still reports as "started".
        try
        {
            mLoggerFactory = CreateLoggerFactory();
            mLog = mLoggerFactory.CreateLogger<App>();

            mMenuModel = new McpServiceMenuModel(new McpServiceController());
            mTrayIcon = (TaskbarIcon) FindResource(TrayIconResourceKey);

            // Efficiency Mode (EcoQoS) is desirable here: the tray is an idle background
            // controller, NOT a host for the MCP server (that's a separate Windows service),
            // so throttling its mostly-sleeping process is a win. Set explicitly rather than
            // relying on the ForceCreate() default.
            mTrayIcon.ForceCreate(enablesEfficiencyMode: true);

            PopulateClientMenus();

            mTrayIcon.ContextMenu.Opened += (_, _) => SyncMenu();
            SyncMenu();
            mLog.LogInformation(StartedLogMessage);

            // Clients installed (or first-launched) after SaddleRAG are missed by the
            // installer's one-shot detected-only registration. Re-run detected registration
            // on every tray startup so a newly-installed agent is picked up on the next login.
            // Idempotent (rewrites the same entry) and skips undetected agents; fire-and-forget
            // so a slow or failing config write never blocks startup.
            _ = AutoRegisterDetectedClientsAsync();
        }
        catch (Exception ex)
        {
            mLog?.LogError(ex, StartupFailedLogMessage);
            MessageBox.Show($"{StartupFailedPrefix}{ex.Message}",
                            BalloonTitle,
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task AutoRegisterDetectedClientsAsync()
    {
        try
        {
            string summary = await mClientService.InstallAsync(null, CancellationToken.None);
            if (summary.Contains(RegistrationFailedMarker, StringComparison.Ordinal))
                mLog?.LogWarning(AutoRegisterPartialFailLogMessage, summary);
            else
                mLog?.LogInformation(AutoRegisterLogMessage, summary);
        }
        catch (Exception ex)
        {
            mLog?.LogWarning(ex, AutoRegisterFailedLogMessage);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Clean exit auto-disposes via DisposeAfterExit; this explicit call mirrors the
        // official sample and is the recommended "cleaner" path.
        mTrayIcon?.Dispose();
        mLoggerFactory?.Dispose();
        base.OnExit(e);
    }

    private static ILoggerFactory CreateLoggerFactory()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   LogDirName,
                                   LogFileName);
        return LoggerFactory.Create(builder => builder.AddProvider(new FileLoggerProvider(path)));
    }

    private void PopulateClientMenus()
    {
        MenuItem? installMenu = FindMenuItem(InstallMenuName);
        MenuItem? uninstallMenu = FindMenuItem(UninstallMenuName);
        if (installMenu is not null)
            BuildClientSubmenu(installMenu, OnInstallClient);
        if (uninstallMenu is not null)
            BuildClientSubmenu(uninstallMenu, OnUninstallClient);
    }

    private static void BuildClientSubmenu(MenuItem parent, RoutedEventHandler handler)
    {
        parent.Items.Clear();
        foreach (ClientWriterCatalog.ClientDescriptor descriptor in ClientWriterCatalog.All)
        {
            MenuItem item = new() { Header = descriptor.DisplayName, Tag = descriptor.Key };
            item.Click += handler;
            parent.Items.Add(item);
        }

        parent.Items.Add(new Separator());
        MenuItem allItem = new() { Header = AllItemHeader, Tag = AllKey };
        allItem.Click += handler;
        parent.Items.Add(allItem);
    }

    private void SyncMenu()
    {
        if (mMenuModel is not null && mTrayIcon?.ContextMenu is not null)
        {
            mMenuModel.Refresh();
            mTrayIcon.ToolTipText = mMenuModel.Tooltip;
            UpdateIcon(mMenuModel.State);
            // x:Name on items inside Application.Resources does NOT generate code-behind
            // fields, and index-based access breaks if a Separator/order changes. Look up
            // by name instead.
            MenuItem? startItem = FindMenuItem(StartItemName);
            MenuItem? stopItem = FindMenuItem(StopItemName);
            if (startItem is not null)
                startItem.IsEnabled = mMenuModel.CanStart;
            if (stopItem is not null)
                stopItem.IsEnabled = mMenuModel.CanStop;
        }
    }

    private MenuItem? FindMenuItem(string name)
    {
        MenuItem? res = null;
        if (mTrayIcon?.ContextMenu is not null)
            res = mTrayIcon.ContextMenu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == name);
        return res;
    }

    private void UpdateIcon(McpServiceState state)
    {
        // Reflect run state with a colored status dot on the logo, via a pre-rendered state
        // .ico exposed as a pack:// URI BitmapImage — the same URI-backed path the base icon
        // already uses. Do NOT feed IconSource a runtime-rendered bitmap; that crashes the
        // process inside H.NotifyIcon's async conversion on a task no try/catch can observe.
        // Loading is guarded best-effort and any failure is logged, never swallowed.
        if (mTrayIcon is not null)
        {
            try
            {
                mTrayIcon.IconSource = TrayIconRenderer.ForState(state);
            }
            catch (Exception ex)
            {
                mLog?.LogWarning(ex, IconRenderFailedLogMessage);
            }
        }
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        McpServiceMenuModel? model = mMenuModel;
        if (model is not null)
            RunGuarded(model.Start, StartingMessage);
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        McpServiceMenuModel? model = mMenuModel;
        if (model is not null)
            RunGuarded(model.Stop, StoppingMessage);
    }

    private void OnOpenDashboard(object sender, RoutedEventArgs e)
    {
        RunGuarded(
            () => Process.Start(new ProcessStartInfo(DashboardUrl) { UseShellExecute = true }),
            OpeningDashboardMessage);
    }

    private async void OnInstallClient(object sender, RoutedEventArgs e)
    {
        string key = ((MenuItem) sender).Tag as string ?? AllKey;
        try
        {
            string summary = await mClientService.InstallAsync(NormalizeKey(key), CancellationToken.None);
            mLog?.LogInformation(InstallLogMessage, key, summary);
            ShowBalloon(summary);
        }
        catch (Exception ex)
        {
            mLog?.LogError(ex, InstallFailedLogMessage, key);
            ShowBalloon($"{InstallFailedPrefix}{ex.Message}");
        }
    }

    private async void OnUninstallClient(object sender, RoutedEventArgs e)
    {
        string key = ((MenuItem) sender).Tag as string ?? AllKey;
        try
        {
            string summary = await mClientService.UninstallAsync(NormalizeKey(key), CancellationToken.None);
            mLog?.LogInformation(UninstallLogMessage, key, summary);
            ShowBalloon(summary);
        }
        catch (Exception ex)
        {
            mLog?.LogError(ex, UninstallFailedLogMessage, key);
            ShowBalloon($"{UninstallFailedPrefix}{ex.Message}");
        }
    }

    private async void OnStatus(object sender, RoutedEventArgs e)
    {
        try
        {
            string summary = await mClientService.StatusAsync(CancellationToken.None);
            ShowOwnedDialog(summary, StatusTitle);
        }
        catch (Exception ex)
        {
            mLog?.LogError(ex, StatusFailedLogMessage);
            ShowBalloon($"{StatusFailedPrefix}{ex.Message}");
        }
    }

    private static void ShowOwnedDialog(string message, string title)
    {
        // A tray app has no main window, so an ownerless MessageBox opens unactivated and the
        // closing context menu's input dismisses it before the user can read it. Give it a
        // tiny, off-screen, topmost owner window that is activated first, so the dialog stays
        // modal and on top until the user clicks OK. The owner is closed in finally.
        Window owner = new()
                           {
                               Width = 1,
                               Height = 1,
                               Left = -10000,
                               Top = -10000,
                               ShowInTaskbar = false,
                               WindowStyle = WindowStyle.None,
                               Topmost = true,
                               ShowActivated = true
                           };
        try
        {
            owner.Show();
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            owner.Close();
        }
    }

    private static string? NormalizeKey(string key) =>
        string.Equals(key, AllKey, StringComparison.OrdinalIgnoreCase) ? null : key;

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    private void RunGuarded(Action action, string workingMessage)
    {
        try
        {
            action();
            SyncMenu();
        }
        catch (Exception ex)
        {
            mLog?.LogError(ex, ActionFailedLogMessage, workingMessage);
            ShowBalloon($"{workingMessage}{ActionFailedSuffix}{ex.Message}");
        }
    }

    private void ShowBalloon(string message)
    {
        mTrayIcon?.ShowNotification(title: BalloonTitle, message: message);
    }
}
