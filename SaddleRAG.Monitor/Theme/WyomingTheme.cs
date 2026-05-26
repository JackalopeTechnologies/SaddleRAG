// WyomingTheme.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using MudBlazor;

#endregion

namespace SaddleRAG.Monitor.Theme;

public static class WyomingTheme
{
    public static MudTheme Create()
    {
        return new MudTheme
                   {
                       PaletteLight = new PaletteLight
                                          {
                                              Primary = BrownPrimary,
                                              Secondary = GoldSecondary,
                                              Background = CreamBg,
                                              AppbarBackground = BrownPrimary,
                                              AppbarText = Colors.Shades.White
                                          },
                       PaletteDark = new PaletteDark
                                         {
                                             Primary = GoldSecondary,
                                             Secondary = BrownPrimary
                                         }
                   };
    }

    private const string BrownPrimary = "#492F24";
    private const string GoldSecondary = "#FFC425";
    private const string CreamBg = "#FDF8F3";
}
