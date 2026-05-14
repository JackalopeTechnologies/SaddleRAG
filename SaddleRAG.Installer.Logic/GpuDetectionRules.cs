// GpuDetectionRules.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

namespace SaddleRAG.Installer.Logic;

/// <summary>
///     Adapter-classification rules used by the installer's
///     <c>CheckGpuCapability.js</c> custom action to decide whether a
///     machine has a real DirectX 12-capable GPU or only a Microsoft
///     fallback adapter. The MSI runs JScript, not .NET, so the live
///     installer cannot call this class — the rules are mirrored
///     line-for-line in <c>CheckGpuCapability.js</c>. This C# copy
///     exists solely so the heuristic is unit-testable; when the rules
///     change, update both sides.
///     <para>
///         "Microsoft fallback adapter" covers the Basic Display
///         Adapter, the Remote / Indirect Display drivers (RDP-only
///         sessions), and the Hyper-V Video adapter (VMs without a
///         dedicated GPU passthrough). None of these expose DirectML.
///     </para>
/// </summary>
public static class GpuDetectionRules
{
    /// <summary>
    ///     Returns true when the adapter identified by
    ///     <paramref name="name" /> + <paramref name="pnpDeviceId" />
    ///     is a Microsoft fallback adapter (no DirectML). Both inputs
    ///     are case-tolerant: name compares case-insensitively as a
    ///     substring, PnP device ID compares case-insensitively as a
    ///     prefix. Null / empty inputs are treated as fallback
    ///     (defensive: an adapter with no identifiers is not a real
    ///     GPU).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Pass the raw <c>Win32_VideoController</c> string fields
    ///         exactly as WMI / Get-CimInstance returns them. Internal
    ///         normalization handles the case folding; pre-lowercasing
    ///         the <paramref name="pnpDeviceId" /> or pre-uppercasing
    ///         the <paramref name="name" /> would silently miss every
    ///         match because the asymmetric prefix-vs-substring
    ///         comparisons fold the two parameters in opposite
    ///         directions internally.
    ///     </para>
    ///     <para>
    ///         The asymmetry is invisible from the <c>(string?, string?)</c>
    ///         signature: <paramref name="name" /> is folded to
    ///         lowercase and matched as a substring (so any of the four
    ///         Microsoft-fallback name fragments anywhere in the
    ///         display name flags the adapter); <paramref name="pnpDeviceId" />
    ///         is folded to uppercase and matched as a prefix against the
    ///         two PnP roots this helper recognizes today
    ///         (<c>ROOT\BASICDISPLAY</c>, <c>ROOT\INDIRECTDISPLAY</c>).
    ///         A future Windows release that places a new fallback adapter
    ///         under a different <c>ROOT\</c> subtree would require an
    ///         entry in <c>smFallbackPnpPrefixes</c> on both sides of the
    ///         JScript / C# mirror.
    ///     </para>
    /// </remarks>
    public static bool IsMicrosoftFallbackAdapter(string? name, string? pnpDeviceId)
    {
        string lowerName  = (name ?? string.Empty).ToLowerInvariant();
        string upperPnp   = (pnpDeviceId ?? string.Empty).ToUpperInvariant();

        bool noIdentifier = lowerName.Length == 0 && upperPnp.Length == 0;
        bool nameMatch    = MatchesAny(lowerName, smFallbackNameFragments);
        bool pnpMatch     = StartsWithAny(upperPnp, smFallbackPnpPrefixes);

        bool result = noIdentifier || nameMatch || pnpMatch;
        return result;
    }

    private static bool MatchesAny(string lowerHaystack, IReadOnlyList<string> needles)
    {
        bool result = false;
        if (lowerHaystack.Length > 0)
        {
            for (int i = 0; i < needles.Count && !result; i++)
            {
                if (lowerHaystack.Contains(needles[i], StringComparison.Ordinal))
                    result = true;
            }
        }
        return result;
    }

    private static bool StartsWithAny(string upperHaystack, IReadOnlyList<string> prefixes)
    {
        bool result = false;
        if (upperHaystack.Length > 0)
        {
            for (int i = 0; i < prefixes.Count && !result; i++)
            {
                if (upperHaystack.StartsWith(prefixes[i], StringComparison.Ordinal))
                    result = true;
            }
        }
        return result;
    }

    private static readonly IReadOnlyList<string> smFallbackNameFragments =
    [
        NameFragmentBasicDisplay,
        NameFragmentRemoteDisplay,
        NameFragmentIndirectDisplay,
        NameFragmentHyperVVideo
    ];

    private static readonly IReadOnlyList<string> smFallbackPnpPrefixes =
    [
        PnpPrefixBasicDisplay,
        PnpPrefixIndirectDisplay
    ];

    private const string NameFragmentBasicDisplay    = "microsoft basic display";
    private const string NameFragmentRemoteDisplay   = "microsoft remote display";
    private const string NameFragmentIndirectDisplay = "microsoft indirect display";
    private const string NameFragmentHyperVVideo     = "microsoft hyper-v video";
    private const string PnpPrefixBasicDisplay       = @"ROOT\BASICDISPLAY";
    private const string PnpPrefixIndirectDisplay    = @"ROOT\INDIRECTDISPLAY";
}
