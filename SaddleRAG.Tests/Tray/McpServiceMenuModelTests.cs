// McpServiceMenuModelTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class McpServiceMenuModelTests
{
    private sealed class FakeController : IMcpServiceController
    {
        public McpServiceState State { get; set; } = McpServiceState.Stopped;
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public McpServiceState GetState() => State;
        public void Start() { StartCalls++; State = McpServiceState.Running; }
        public void Stop() { StopCalls++; State = McpServiceState.Stopped; }
    }

    [Fact]
    public void WhenStoppedStartEnabledStopDisabled()
    {
        FakeController fake = new() { State = McpServiceState.Stopped };
        McpServiceMenuModel model = new(fake);

        model.Refresh();

        Assert.True(model.CanStart);
        Assert.False(model.CanStop);
    }

    [Fact]
    public void WhenRunningStopEnabledStartDisabled()
    {
        FakeController fake = new() { State = McpServiceState.Running };
        McpServiceMenuModel model = new(fake);

        model.Refresh();

        Assert.False(model.CanStart);
        Assert.True(model.CanStop);
    }

    [Fact]
    public void WhenNotInstalledBothDisabled()
    {
        FakeController fake = new() { State = McpServiceState.NotInstalled };
        McpServiceMenuModel model = new(fake);

        model.Refresh();

        Assert.False(model.CanStart);
        Assert.False(model.CanStop);
    }

    [Fact]
    public void StartInvokesControllerAndRefreshes()
    {
        FakeController fake = new() { State = McpServiceState.Stopped };
        McpServiceMenuModel model = new(fake);

        model.Start();

        Assert.Equal(1, fake.StartCalls);
        Assert.True(model.CanStop);
        Assert.False(model.CanStart);
    }
}
