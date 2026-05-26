// MainLayoutTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Mcp.Monitor;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MainLayoutTests
{
    private sealed class TestableMainLayout : MainLayoutBase
    {
        public bool DrawerOpenForTest => DrawerOpen;
        public void InvokeToggle() => ToggleDrawer();
    }

    [Fact]
    public void ToggleDrawerFlipsTheDrawerOpenFlag()
    {
        var layout = new TestableMainLayout();
        Assert.True(layout.DrawerOpenForTest);
        layout.InvokeToggle();
        Assert.False(layout.DrawerOpenForTest);
        layout.InvokeToggle();
        Assert.True(layout.DrawerOpenForTest);
    }
}
