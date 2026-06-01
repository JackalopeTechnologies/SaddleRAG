// MainLayout.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.AspNetCore.Components;

#endregion

namespace SaddleRAG.Mcp.Monitor;

public abstract class MainLayoutBase : LayoutComponentBase
{
    protected bool DrawerOpen { get; set; } = true;

    protected void ToggleDrawer()
    {
        DrawerOpen = !DrawerOpen;
    }
}
