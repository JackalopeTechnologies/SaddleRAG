// PackageWxsLaunchConditionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using System.Xml.Linq;

#endregion

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     Pins the WiX <c>&lt;Launch&gt;</c> condition string in
///     <c>SaddleRAG.Installer/Package.wxs</c> so a routine WiX refactor
///     can't silently change the install gate. The condition mixes
///     intentional MSI-condition coercion rules ("0" quoted as a string
///     vs 18362 unquoted as a number) — getting either side wrong
///     produces an MSI that quietly allows or blocks the wrong machines.
///     Manual verification (booting a fabricated old Windows build)
///     only catches whether the gate fires; this test catches whether
///     the source-of-truth string still says what it was meant to say.
/// </summary>
public sealed class PackageWxsLaunchConditionTests
{
    [Fact]
    public void LaunchConditionMatchesExpectedString()
    {
        XElement launch = LoadLaunchElement();
        string? actual = (string?) launch.Attribute("Condition");
        Assert.Equal(ExpectedLaunchCondition, actual);
    }

    [Fact]
    public void LaunchMessageMentionsWindows10Build()
    {
        XElement launch = LoadLaunchElement();
        string? message = (string?) launch.Attribute("Message");
        Assert.NotNull(message);
        Assert.Contains("Windows 10", message);
        Assert.Contains("18362", message);
    }

    [Fact]
    public void CheckGpuCapabilityIsScheduledBeforeLaunchConditionsInBothSequences()
    {
        XDocument doc = LoadPackageWxs();
        XNamespace ns = WixNamespace;

        bool inUiSequence = HasCheckGpuCapabilityBeforeLaunchConditions(doc, ns, "InstallUISequence");
        bool inExecuteSequence = HasCheckGpuCapabilityBeforeLaunchConditions(doc, ns, "InstallExecuteSequence");

        Assert.True(inUiSequence, "CheckGpuCapability must be scheduled Before='LaunchConditions' in InstallUISequence.");
        Assert.True(inExecuteSequence, "CheckGpuCapability must be scheduled Before='LaunchConditions' in InstallExecuteSequence.");
    }

    [Fact]
    public void OnnxExecutionProviderPropertyHasNoDefaultValue()
    {
        XDocument doc = LoadPackageWxs();
        XNamespace ns = WixNamespace;

        XElement? property = doc.Descendants(ns + "Property")
                                .FirstOrDefault(e => (string?) e.Attribute("Id") == "ONNX_EXECUTION_PROVIDER");

        Assert.NotNull(property);

        // The "empty == auto-detect" semantic depends on this property
        // arriving as an empty string. A default Value attribute would
        // make the CA's empty-guard a no-op and break command-line
        // overrides on silent installs.
        Assert.Null(property.Attribute("Value"));
    }

    private static XElement LoadLaunchElement()
    {
        XDocument doc = LoadPackageWxs();
        XNamespace ns = WixNamespace;
        XElement? launch = doc.Descendants(ns + "Launch").FirstOrDefault();
        if (launch == null)
            throw new InvalidOperationException("No <Launch> element found in Package.wxs.");
        return launch;
    }

    private static bool HasCheckGpuCapabilityBeforeLaunchConditions(XDocument doc, XNamespace ns, string sequenceName)
    {
        XElement? sequence = doc.Descendants(ns + sequenceName).FirstOrDefault();
        bool result = false;
        if (sequence != null)
        {
            XElement? entry = sequence.Elements(ns + "Custom")
                                      .FirstOrDefault(e => (string?) e.Attribute("Action") == "CheckGpuCapability"
                                                           && (string?) e.Attribute("Before") == "LaunchConditions"
                                                          );
            result = entry != null;
        }
        return result;
    }

    private static XDocument LoadPackageWxs()
    {
        string path = ResolvePackageWxsPath();
        XDocument doc = XDocument.Load(path);
        return doc;
    }

    private static string ResolvePackageWxsPath()
    {
        string testBinDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        DirectoryInfo? dir = new DirectoryInfo(testBinDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SaddleRAG.slnx")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("Could not locate repository root from test binary directory.");
        string wxsPath = Path.Combine(dir.FullName, "SaddleRAG.Installer", "Package.wxs");
        if (!File.Exists(wxsPath))
            throw new InvalidOperationException($"Package.wxs not found at '{wxsPath}'.");
        return wxsPath;
    }

    private const string ExpectedLaunchCondition =
        "Installed OR WINDOWSBUILDNUMBER = \"0\" OR WINDOWSBUILDNUMBER >= 18362";

    private static readonly XNamespace WixNamespace = "http://wixtoolset.org/schemas/v4/wxs";
}
