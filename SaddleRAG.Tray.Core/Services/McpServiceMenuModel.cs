// McpServiceMenuModel.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tray.Services;

public sealed class McpServiceMenuModel
{
    private const string TooltipRunning = "SaddleRAG MCP — running";
    private const string TooltipStopped = "SaddleRAG MCP — stopped";
    private const string TooltipNotInstalled = "SaddleRAG MCP — not installed";
    private const string TooltipTransitioning = "SaddleRAG MCP — working…";
    private const string TooltipUnknown = "SaddleRAG MCP";

    private readonly IMcpServiceController mController;

    public McpServiceMenuModel(IMcpServiceController controller)
    {
        mController = controller;
        State = controller.GetState();
    }

    public McpServiceState State { get; private set; } = McpServiceState.Unknown;

    public bool CanStart => State == McpServiceState.Stopped;

    public bool CanStop => State == McpServiceState.Running;

    public string Tooltip => State switch
                             {
                                 McpServiceState.Running => TooltipRunning,
                                 McpServiceState.Stopped => TooltipStopped,
                                 McpServiceState.NotInstalled => TooltipNotInstalled,
                                 McpServiceState.Transitioning => TooltipTransitioning,
                                 _ => TooltipUnknown
                             };

    public void Refresh()
    {
        State = mController.GetState();
    }

    public void Start()
    {
        mController.Start();
        Refresh();
    }

    public void Stop()
    {
        mController.Stop();
        Refresh();
    }
}
