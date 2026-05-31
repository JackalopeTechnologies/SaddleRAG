// App.xaml.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using SaddleRAG.ClientIntegration;
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
    private const string StatusTitle = "SaddleRAG — client status";
    private const string ActionFailedSuffix = " failed: ";
    private const string IconRenderFailedMessage = "Status icon update failed: ";

    private TaskbarIcon? mTrayIcon;
    private McpServiceMenuModel? mMenuModel;
    private readonly TrayClientService mClientService = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Clean exit auto-disposes via DisposeAfterExit; this explicit call mirrors the
        // official sample and is the recommended "cleaner" path.
        mTrayIcon?.Dispose();
        base.OnExit(e);
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
        if (mTrayIcon is not null)
        {
            try
            {
                mTrayIcon.IconSource = TrayIconRenderer.ForState(state);
            }
            catch (Exception ex)
            {
                ShowBalloon($"{IconRenderFailedMessage}{ex.Message}");
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
            ShowBalloon(summary);
        }
        catch (Exception ex)
        {
            ShowBalloon($"{InstallFailedPrefix}{ex.Message}");
        }
    }

    private async void OnUninstallClient(object sender, RoutedEventArgs e)
    {
        string key = ((MenuItem) sender).Tag as string ?? AllKey;
        try
        {
            string summary = await mClientService.UninstallAsync(NormalizeKey(key), CancellationToken.None);
            ShowBalloon(summary);
        }
        catch (Exception ex)
        {
            ShowBalloon($"{UninstallFailedPrefix}{ex.Message}");
        }
    }

    private async void OnStatus(object sender, RoutedEventArgs e)
    {
        try
        {
            string summary = await mClientService.StatusAsync(CancellationToken.None);
            MessageBox.Show(summary, StatusTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowBalloon($"{StatusFailedPrefix}{ex.Message}");
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
            ShowBalloon($"{workingMessage}{ActionFailedSuffix}{ex.Message}");
        }
    }

    private void ShowBalloon(string message)
    {
        mTrayIcon?.ShowNotification(title: BalloonTitle, message: message);
    }
}
