// MainLayoutTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Mcp.Monitor;

#endregion

namespace SaddleRAG.Tests.Monitor;

public sealed class MainLayoutTests
{
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

    private sealed class TestableMainLayout : MainLayoutBase
    {
        public bool DrawerOpenForTest => DrawerOpen;
        public void InvokeToggle() => ToggleDrawer();
    }
}
