// OllamaKeepAliveInstallTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Xml.Linq;

#endregion

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     The installer used to set OLLAMA_KEEP_ALIVE=-1 machine-wide, which kept every
///     program's Ollama models in GPU memory forever — not just SaddleRAG's, and even
///     on the default all-ONNX setup that does not use Ollama at all. SaddleRAG now
///     asks for keep-alive on its own Ollama requests instead. These tests pin that
///     the install paths no longer set the variable, while uninstall still removes
///     a value an older version set (only if unchanged since).
/// </summary>
public sealed class OllamaKeepAliveInstallTests
{
    [Fact]
    public void MsiNoLongerSetsOllamaKeepAliveMachineWide()
    {
        XDocument doc = LoadPackageWxs();

        Assert.DoesNotContain(doc.Descendants(smWix + "CustomAction"), e => HasAttribute(e, "Id", SetActionId));
        Assert.DoesNotContain(doc.Descendants(smWix + "SetProperty"), e => HasAttribute(e, "Id", SetActionId));
        Assert.DoesNotContain(doc.Descendants(smWix + "Custom"), e => HasAttribute(e, "Action", SetActionId));
        Assert.DoesNotContain(doc.Descendants(smWix + "File"), e => HasAttribute(e, "Source", SetScriptName));
    }

    [Fact]
    public void MsiUninstallStillRemovesAKeepAliveAnOlderVersionSet()
    {
        XDocument doc = LoadPackageWxs();

        Assert.Contains(doc.Descendants(smWix + "Custom"),
                        e => HasAttribute(e, "Action", UnsetActionId) && HasAttribute(e, "Condition", RemoveAllCondition)
                       );
        Assert.Contains(doc.Descendants(smWix + "File"), e => HasAttribute(e, "Source", UnsetScriptName));
    }

    [Fact]
    public void MsiOllamaDialogNoLongerPromisesToSetKeepAlive()
    {
        XDocument doc = LoadPackageWxs();

        Assert.DoesNotContain(doc.Descendants(smWix + "Control"), e => HasAttribute(e, "Id", "KeepAliveText"));
        Assert.DoesNotContain(doc.Descendants(smWix + "CustomAction"), e => HasAttribute(e, "Id", CheckActionId));
        Assert.DoesNotContain(doc.Descendants(smWix + "Property"),
                              e => ((string?) e.Attribute("Id"))?.StartsWith("OLLAMA_KEEPALIVE", StringComparison.Ordinal) == true
                             );
    }

    [Fact]
    public void ManualServiceInstallScriptNoLongerSetsOllamaKeepAlive()
    {
        string? root = InstallerSourceTreeResolver.TryResolveRepositoryRoot();
        string? path = root == null ? null : Path.Combine(root, "SaddleRAG.Mcp", "install-service.ps1");
        if (path == null || !File.Exists(path))
            Assert.Skip("SaddleRAG.Mcp/install-service.ps1 is not in this source tree.");
        Assert.NotNull(path);

        Assert.DoesNotContain("OLLAMA_KEEP_ALIVE", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private static bool HasAttribute(XElement element, string name, string value) =>
        string.Equals((string?) element.Attribute(name), value, StringComparison.Ordinal);

    private static XDocument LoadPackageWxs()
    {
        string? path = InstallerSourceTreeResolver.TryResolveInstallerFile("Package.wxs");
        if (path == null)
            Assert.Skip("SaddleRAG.Installer/Package.wxs is not in this source tree.");
        Assert.NotNull(path);
        return XDocument.Load(path);
    }

    private const string SetActionId = "SetOllamaKeepAlive";
    private const string UnsetActionId = "UnsetOllamaKeepAlive";
    private const string CheckActionId = "CheckOllamaKeepAlive";
    private const string SetScriptName = "SetOllamaKeepAlive.ps1";
    private const string UnsetScriptName = "UnsetOllamaKeepAlive.ps1";
    private const string RemoveAllCondition = "REMOVE = \"ALL\"";

    private static readonly XNamespace smWix = "http://wixtoolset.org/schemas/v4/wxs";
}
