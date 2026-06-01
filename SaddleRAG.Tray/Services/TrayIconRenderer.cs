// TrayIconRenderer.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Windows.Media;
using System.Windows.Media.Imaging;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tray;

/// <summary>
///     Supplies the tray icon for a given MCP service run-state: the SaddleRAG logo with a
///     colored status dot in the corner (green = running, red = stopped, gray = not
///     installed, orange = transitioning), pre-rendered as four .ico assets shipped as WPF
///     resources.
///     <para>
///         The icons are returned as <see cref="BitmapImage" />s built from
///         <c>pack://</c> URIs and assigned to <c>TaskbarIcon.IconSource</c>. This is the
///         SAME path the base icon already uses successfully
///         (<c>IconSource="/Resources/saddlerag-icon.ico"</c>): H.NotifyIcon's
///         ImageSource→stream conversion supports a URI-backed bitmap. It must NOT be fed a
///         runtime-rendered <c>RenderTargetBitmap</c> or a stream-created
///         <c>BitmapFrame</c> — those have no source URI and throw inside
///         <c>ToStreamAsync</c> on a dispatcher task no caller try/catch can observe,
///         crashing the process.
///     </para>
/// </summary>
internal static class TrayIconRenderer
{
    private const string PackUriFormat = "pack://application:,,,/Resources/tray-{0}.ico";
    private const string RunningName = "running";
    private const string StoppedName = "stopped";
    private const string NotInstalledName = "notinstalled";
    private const string TransitioningName = "transitioning";

    private static readonly Dictionary<McpServiceState, ImageSource> smCache = [];
    private static readonly Lock smLock = new();

    public static ImageSource ForState(McpServiceState state)
    {
        ImageSource res;
        lock (smLock)
        {
            if (!smCache.TryGetValue(state, out ImageSource? cached))
            {
                cached = LoadIcon(StateName(state));
                smCache[state] = cached;
            }
            res = cached;
        }
        return res;
    }

    private static ImageSource LoadIcon(string name)
    {
        Uri uri = new(string.Format(PackUriFormat, name), UriKind.Absolute);
        BitmapImage image = new();
        image.BeginInit();
        image.UriSource = uri;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string StateName(McpServiceState state) => state switch
    {
        McpServiceState.Running => RunningName,
        McpServiceState.Stopped => StoppedName,
        McpServiceState.NotInstalled => NotInstalledName,
        _ => TransitioningName
    };
}
