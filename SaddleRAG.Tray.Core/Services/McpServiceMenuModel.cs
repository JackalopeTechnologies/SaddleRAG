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

    private const string ServiceName = "SaddleRAGMcp";

    private static readonly string[] smServiceNames = [ServiceName];

    private readonly IMcpServiceController mController;
    private readonly IServiceActionRunner mRunner;

    public McpServiceMenuModel(IMcpServiceController controller, IServiceActionRunner runner)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(runner);

        mController = controller;
        mRunner = runner;
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

    /// <summary>Starts the MCP service through the elevated script, then re-reads its state.</summary>
    public Task<ServiceActionOutcome> StartAsync(CancellationToken ct) => RunAsync(ServiceAction.Start, ct);

    /// <summary>Stops the MCP service through the elevated script, then re-reads its state.</summary>
    public Task<ServiceActionOutcome> StopAsync(CancellationToken ct) => RunAsync(ServiceAction.Stop, ct);

    private async Task<ServiceActionOutcome> RunAsync(ServiceAction action, CancellationToken ct)
    {
        ServiceActionOutcome result = await mRunner.RunAsync(smServiceNames, action, ct);
        Refresh();
        return result;
    }
}
