// WyomingTheme.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

using MudBlazor;

namespace SaddleRAG.Monitor.Theme;

public static class WyomingTheme
{
    private const string BrownPrimary  = "#492F24";
    private const string GoldSecondary = "#FFC425";
    private const string CreamBg       = "#FDF8F3";

    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary          = BrownPrimary,
            Secondary        = GoldSecondary,
            Background       = CreamBg,
            AppbarBackground = BrownPrimary,
            AppbarText       = Colors.Shades.White
        },
        PaletteDark = new PaletteDark
        {
            Primary   = GoldSecondary,
            Secondary = BrownPrimary
        }
    };
}
