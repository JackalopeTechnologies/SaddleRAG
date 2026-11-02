// McpServiceMenuModelTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class McpServiceMenuModelTests
{
    private sealed class FakeController : IMcpServiceController
    {
        public McpServiceState State { get; set; } = McpServiceState.Stopped;

        public McpServiceState GetState() => State;
    }

    private sealed class FakeRunner : IServiceActionRunner
    {
        public FakeRunner(FakeController controller)
        {
            mController = controller;
        }

        private readonly FakeController mController;

        public ServiceActionOutcome Outcome { get; set; } = ServiceActionOutcome.Succeeded;
        public List<(IReadOnlyList<string> Names, ServiceAction Action)> Calls { get; } = [];

        public Task<ServiceActionOutcome> RunAsync(IReadOnlyList<string> serviceNames,
                                                   ServiceAction action,
                                                   CancellationToken ct)
        {
            Calls.Add((serviceNames, action));
            if (Outcome == ServiceActionOutcome.Succeeded)
                mController.State = action == ServiceAction.Start ? McpServiceState.Running : McpServiceState.Stopped;
            return Task.FromResult(Outcome);
        }
    }

    private static McpServiceMenuModel NewModel(FakeController fake, out FakeRunner runner)
    {
        runner = new FakeRunner(fake);
        return new McpServiceMenuModel(fake, runner);
    }

    [Fact]
    public void WhenStoppedStartEnabledStopDisabled()
    {
        FakeController fake = new() { State = McpServiceState.Stopped };
        McpServiceMenuModel model = NewModel(fake, out _);

        model.Refresh();

        Assert.True(model.CanStart);
        Assert.False(model.CanStop);
    }

    [Fact]
    public void WhenRunningStopEnabledStartDisabled()
    {
        FakeController fake = new() { State = McpServiceState.Running };
        McpServiceMenuModel model = NewModel(fake, out _);

        model.Refresh();

        Assert.False(model.CanStart);
        Assert.True(model.CanStop);
    }

    [Fact]
    public void WhenNotInstalledBothDisabled()
    {
        FakeController fake = new() { State = McpServiceState.NotInstalled };
        McpServiceMenuModel model = NewModel(fake, out _);

        model.Refresh();

        Assert.False(model.CanStart);
        Assert.False(model.CanStop);
    }

    [Fact]
    public async Task StartAsyncAsksTheRunnerToStartTheMcpServiceAndRefreshes()
    {
        FakeController fake = new() { State = McpServiceState.Stopped };
        McpServiceMenuModel model = NewModel(fake, out FakeRunner runner);

        ServiceActionOutcome outcome = await model.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceActionOutcome.Succeeded, outcome);
        (IReadOnlyList<string> names, ServiceAction action) = Assert.Single(runner.Calls);
        Assert.Equal(["SaddleRAGMcp"], names);
        Assert.Equal(ServiceAction.Start, action);
        Assert.True(model.CanStop);
        Assert.False(model.CanStart);
    }

    [Fact]
    public async Task StopAsyncAsksTheRunnerToStopTheMcpServiceAndRefreshes()
    {
        FakeController fake = new() { State = McpServiceState.Running };
        McpServiceMenuModel model = NewModel(fake, out FakeRunner runner);

        ServiceActionOutcome outcome = await model.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceActionOutcome.Succeeded, outcome);
        (IReadOnlyList<string> names, ServiceAction action) = Assert.Single(runner.Calls);
        Assert.Equal(["SaddleRAGMcp"], names);
        Assert.Equal(ServiceAction.Stop, action);
        Assert.True(model.CanStart);
        Assert.False(model.CanStop);
    }

    [Fact]
    public async Task AFailedActionLeavesTheStateAsTheServiceReportsIt()
    {
        FakeController fake = new() { State = McpServiceState.Stopped };
        McpServiceMenuModel model = NewModel(fake, out FakeRunner runner);
        runner.Outcome = ServiceActionOutcome.Cancelled;

        ServiceActionOutcome outcome = await model.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceActionOutcome.Cancelled, outcome);
        Assert.True(model.CanStart);
    }

    [Fact]
    public void WhenUnknownTooltipHasNoSuffix()
    {
        FakeController fake = new() { State = McpServiceState.Unknown };
        McpServiceMenuModel model = NewModel(fake, out _);

        Assert.Equal("SaddleRAG MCP", model.Tooltip);
    }
}
