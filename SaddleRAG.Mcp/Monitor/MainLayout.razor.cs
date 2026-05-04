// MainLayout.razor.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

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
