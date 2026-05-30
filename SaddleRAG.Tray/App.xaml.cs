// App.xaml.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
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

    private const string StartingMessage = "Starting MCP service…";
    private const string StoppingMessage = "Stopping MCP service…";
    private const string OpeningDashboardMessage = "Opening dashboard…";
    private const string HelperInstallFailedPrefix = "Helper install failed: ";
    private const string ActionFailedSuffix = " failed: ";

    private TaskbarIcon? mTrayIcon;
    private McpServiceMenuModel? mMenuModel;
    private readonly HelperInstaller mHelperInstaller = new();

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

    private void SyncMenu()
    {
        if (mMenuModel is not null && mTrayIcon?.ContextMenu is not null)
        {
            mMenuModel.Refresh();
            mTrayIcon.ToolTipText = mMenuModel.Tooltip;
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

    private void OnInstallClaudeCode(object sender, RoutedEventArgs e) => InstallHelper(HelperClient.ClaudeCode);
    private void OnInstallClaudeDesktop(object sender, RoutedEventArgs e) => InstallHelper(HelperClient.ClaudeDesktop);
    private void OnInstallVsCode(object sender, RoutedEventArgs e) => InstallHelper(HelperClient.VsCode);
    private void OnInstallCopilot(object sender, RoutedEventArgs e) => InstallHelper(HelperClient.CopilotCli);
    private void OnInstallCodex(object sender, RoutedEventArgs e) => InstallHelper(HelperClient.Codex);
    private void OnInstallAll(object sender, RoutedEventArgs e) => InstallHelper(HelperClient.All);

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    private async void InstallHelper(HelperClient client)
    {
        try
        {
            string summary = await mHelperInstaller.RegisterAsync(client, CancellationToken.None);
            ShowBalloon(summary);
        }
        catch (Exception ex)
        {
            ShowBalloon($"{HelperInstallFailedPrefix}{ex.Message}");
        }
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
