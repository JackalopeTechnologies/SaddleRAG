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
///     Also pins the PatchAppSettings SetProperty argv string so a typo
///     in a parameter name (e.g. <c>-AppsettingsPath</c> vs
///     <c>-AppSettingsPath</c>) is caught at test time instead of
///     during an install when PowerShell's parameter binder silently
///     reports an error the CA's Return="ignore" wrapper swallows.
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
        XNamespace ns = smWixNamespace;

        bool inUiSequence = HasCheckGpuCapabilityBeforeLaunchConditions(doc, ns, InstallUISequenceName);
        bool inExecuteSequence = HasCheckGpuCapabilityBeforeLaunchConditions(doc, ns, InstallExecuteSequenceName);

        Assert.True(inUiSequence, "CheckGpuCapability must be scheduled Before='LaunchConditions' in InstallUISequence.");
        Assert.True(inExecuteSequence, "CheckGpuCapability must be scheduled Before='LaunchConditions' in InstallExecuteSequence.");
    }

    [Fact]
    public void OnnxExecutionProviderPropertyHasNoDefaultValue()
    {
        XDocument doc = LoadPackageWxs();
        XNamespace ns = smWixNamespace;

        // Single() catches a duplicate Property declaration in the WXS — a
        // FirstOrDefault would silently validate only the first match.
        XElement property = doc.Descendants(ns + PropertyElementName)
                               .Single(e => (string?) e.Attribute(IdAttributeName) == OnnxExecutionProviderPropertyId);

        // The "empty == auto-detect" semantic depends on this property
        // arriving as an empty string. A default Value attribute would
        // make the CA's empty-guard a no-op and break command-line
        // overrides on silent installs.
        Assert.Null(property.Attribute(ValueAttributeName));
    }

    [Fact]
    public void PatchAppSettingsSetPropertyContainsAllScriptParameterNames()
    {
        XDocument doc = LoadPackageWxs();
        XNamespace ns = smWixNamespace;

        // The deferred PatchAppSettings CA is driven by a SetProperty whose
        // Value attribute carries the full powershell.exe command line. A
        // typo in any of the five parameter names (e.g., capitalization
        // drift between -AppSettingsPath and the .ps1's param declaration)
        // would fall through to PowerShell's parameter binder and surface
        // only as a "missing parameter" message in the verbose MSI log.
        XElement setProperty = doc.Descendants(ns + SetPropertyElementName)
                                  .Single(e => (string?) e.Attribute(IdAttributeName) == PatchAppSettingsId);

        string? commandLine = (string?) setProperty.Attribute(ValueAttributeName);
        Assert.NotNull(commandLine);

        foreach (string parameterName in smPatchAppSettingsScriptParameters)
            Assert.Contains(parameterName, commandLine);

        // And the script itself must declare each of those parameters,
        // matching by name. This catches a rename on either side without
        // having to actually execute the script.
        string? scriptPath = TryResolveScriptPath();
        if (scriptPath != null)
        {
            string scriptText = File.ReadAllText(scriptPath);
            foreach (string parameterName in smPatchAppSettingsScriptParameters)
            {
                // PowerShell param-block names appear as "[string]$Name" or
                // "[Parameter(...)]\n[type]$Name". Strip the leading '-' from
                // the CA arg form and search for the unprefixed identifier.
                string identifier = parameterName.TrimStart(ParameterPrefixDash);
                Assert.True(scriptText.Contains(identifier, StringComparison.Ordinal),
                            $"PatchAppSettings.ps1 missing param declaration for {parameterName}."
                           );
            }
        }
    }

    private static XElement LoadLaunchElement()
    {
        XDocument doc = LoadPackageWxs();
        XNamespace ns = smWixNamespace;
        // Single() rather than FirstOrDefault so a future PR that adds a
        // second <Launch> element (perfectly legal in WiX) doesn't silently
        // pass these assertions against the wrong one.
        XElement launch = doc.Descendants(ns + LaunchElementName).Single();
        return launch;
    }

    private static bool HasCheckGpuCapabilityBeforeLaunchConditions(XDocument doc, XNamespace ns, string sequenceName)
    {
        XElement? sequence = doc.Descendants(ns + sequenceName).FirstOrDefault();
        bool result = false;
        if (sequence != null)
        {
            XElement? entry = sequence.Elements(ns + CustomElementName)
                                      .FirstOrDefault(e => (string?) e.Attribute(ActionAttributeName) == CheckGpuCapabilityActionId
                                                           && (string?) e.Attribute(BeforeAttributeName) == LaunchConditionsActionId
                                                          );
            result = entry != null;
        }
        return result;
    }

    private static XDocument LoadPackageWxs()
    {
        string? path = TryResolvePackageWxsPath();
        if (path == null)
            Assert.Skip(WxsMissingSkipReason);
        Assert.NotNull(path);
        return XDocument.Load(path);
    }

    private static string? TryResolvePackageWxsPath()
    {
        string testBinDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        DirectoryInfo? dir = new DirectoryInfo(testBinDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, RepositoryRootMarker)))
            dir = dir.Parent;
        string? result = null;
        if (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, InstallerFolderName, WxsFileName);
            if (File.Exists(candidate))
                result = candidate;
        }
        return result;
    }

    private static string? TryResolveScriptPath()
    {
        string testBinDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        DirectoryInfo? dir = new DirectoryInfo(testBinDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, RepositoryRootMarker)))
            dir = dir.Parent;
        string? result = null;
        if (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, InstallerFolderName, PatchScriptFileName);
            if (File.Exists(candidate))
                result = candidate;
        }
        return result;
    }

    private static readonly string[] smPatchAppSettingsScriptParameters =
        [
            "-AppSettingsPath",
        "-ConnectionString",
        "-DatabaseName",
        "-OllamaEndpoint",
        "-ExecutionProvider"
        ];

    private static readonly XNamespace smWixNamespace = "http://wixtoolset.org/schemas/v4/wxs";

    private const string ExpectedLaunchCondition =
        "Installed OR WINDOWSBUILDNUMBER = \"0\" OR WINDOWSBUILDNUMBER >= 18362";

    private const char ParameterPrefixDash = '-';

    private const string PropertyElementName = "Property";
    private const string SetPropertyElementName = "SetProperty";
    private const string LaunchElementName = "Launch";
    private const string CustomElementName = "Custom";
    private const string IdAttributeName = "Id";
    private const string ValueAttributeName = "Value";
    private const string ActionAttributeName = "Action";
    private const string BeforeAttributeName = "Before";

    private const string OnnxExecutionProviderPropertyId = "ONNX_EXECUTION_PROVIDER";
    private const string PatchAppSettingsId = "PatchAppSettings";
    private const string CheckGpuCapabilityActionId = "CheckGpuCapability";
    private const string LaunchConditionsActionId = "LaunchConditions";
    private const string InstallUISequenceName = "InstallUISequence";
    private const string InstallExecuteSequenceName = "InstallExecuteSequence";

    private const string RepositoryRootMarker = "SaddleRAG.slnx";
    private const string InstallerFolderName = "SaddleRAG.Installer";
    private const string WxsFileName = "Package.wxs";
    private const string PatchScriptFileName = "PatchAppSettings.ps1";

    private const string WxsMissingSkipReason =
        "Package.wxs not locatable from test binary directory; the test requires the WiX source to be present in the source tree.";
}
