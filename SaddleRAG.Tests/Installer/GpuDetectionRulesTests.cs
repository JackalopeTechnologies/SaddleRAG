// GpuDetectionRulesTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using SaddleRAG.Installer.Logic;

#endregion

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     Locks the adapter-classification heuristic used by the installer's
///     <c>CheckGpuCapability.js</c> custom action. The live installer runs
///     JScript that mirrors <see cref="GpuDetectionRules" />; these tests
///     are the source-of-truth contract that catches regressions on either
///     side. The names used in <see cref="RealAdapters" /> and
///     <see cref="FallbackAdapters" /> reflect what
///     <c>Win32_VideoController</c> actually reports on common hardware.
/// </summary>
public sealed class GpuDetectionRulesTests
{
    [Theory]
    [MemberData(nameof(RealAdapters))]
    public void IsMicrosoftFallbackAdapterReturnsFalseForRealGpus(string name, string pnpDeviceId)
    {
        bool result = GpuDetectionRules.IsMicrosoftFallbackAdapter(name, pnpDeviceId);
        Assert.False(result, $"Expected real GPU '{name}' (pnp '{pnpDeviceId}') to be classified as non-fallback.");
    }

    [Theory]
    [MemberData(nameof(FallbackAdapters))]
    public void IsMicrosoftFallbackAdapterReturnsTrueForFallbackAdapters(string name, string pnpDeviceId)
    {
        bool result = GpuDetectionRules.IsMicrosoftFallbackAdapter(name, pnpDeviceId);
        Assert.True(result, $"Expected fallback adapter '{name}' (pnp '{pnpDeviceId}') to be classified as fallback.");
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("", null)]
    public void IsMicrosoftFallbackAdapterTreatsEmptyAsFallback(string? name, string? pnpDeviceId)
    {
        bool result = GpuDetectionRules.IsMicrosoftFallbackAdapter(name, pnpDeviceId);
        Assert.True(result, "An adapter with no identifiers must not be treated as a real GPU.");
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", "nvidia geforce rtx 4090")]
    [InlineData("microsoft basic display adapter", "MICROSOFT BASIC DISPLAY ADAPTER")]
    public void IsMicrosoftFallbackAdapterIsCaseInsensitive(string mixedCaseName, string permutedCase)
    {
        bool original = GpuDetectionRules.IsMicrosoftFallbackAdapter(mixedCaseName, pnpDeviceId: null);
        bool permuted = GpuDetectionRules.IsMicrosoftFallbackAdapter(permutedCase, pnpDeviceId: null);
        Assert.Equal(original, permuted);
    }

    [Fact]
    public void IsMicrosoftFallbackAdapterMatchesByNameOrPnpIndependently()
    {
        // Realistic Hyper-V identifiers have a normal-looking PnP path
        // (PCI\\VEN_...) but a fallback-tier Name. Either signal alone
        // should classify as fallback.
        bool nameOnly = GpuDetectionRules.IsMicrosoftFallbackAdapter("Microsoft Hyper-V Video",
                                                                    pnpDeviceId: @"PCI\VEN_1414&DEV_5353"
                                                                   );
        Assert.True(nameOnly);

        // Inverse: a "real" name but a fallback PnP root (someone renamed
        // the adapter or shipped a driver that lies about the name). PnP
        // path is still the authoritative signal here.
        bool pnpOnly = GpuDetectionRules.IsMicrosoftFallbackAdapter("Custom Renamed Adapter",
                                                                   pnpDeviceId: @"ROOT\BASICDISPLAY\0000"
                                                                  );
        Assert.True(pnpOnly);
    }

    public static IEnumerable<TheoryDataRow<string, string>> RealAdapters()
    {
        yield return new("NVIDIA GeForce RTX 4090", @"PCI\VEN_10DE&DEV_2684&SUBSYS_403F1458&REV_A1\4&2DB6BB66&0&0008");
        yield return new("AMD Radeon RX 7900 XTX", @"PCI\VEN_1002&DEV_744C&SUBSYS_E471174B&REV_C8\6&A6BAA32&0&00000019");
        yield return new("Intel(R) Arc(TM) A770 Graphics", @"PCI\VEN_8086&DEV_56A0&SUBSYS_56A08086&REV_08\6&A6BAA32&0&00000018");
        yield return new("Intel(R) UHD Graphics 770", @"PCI\VEN_8086&DEV_4680&SUBSYS_77508086&REV_0C\3&11583659&0&10");
        yield return new("Qualcomm(R) Adreno(TM) X1-85 GPU", @"ACPI\QCOM0C29\0");
    }

    public static IEnumerable<TheoryDataRow<string, string>> FallbackAdapters()
    {
        yield return new("Microsoft Basic Display Adapter", @"ROOT\BASICDISPLAY\0000");
        yield return new("Microsoft Remote Display Adapter", @"ROOT\BASICDISPLAY\0001");
        yield return new("Microsoft Indirect Display Driver", @"ROOT\INDIRECTDISPLAY\0000");
        yield return new("Microsoft Hyper-V Video", @"VMBUS\{DA0A7802-E377-4AAC-8E77-0558EB1073F8}\5&368F2C75&0&{DA0A7802-E377-4AAC-8E77-0558EB1073F8}");
    }
}
