// InstallFolderPermissionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.RegularExpressions;
using System.Xml.Linq;

#endregion

namespace SaddleRAG.Tests.Installer;

/// <summary>
///     The install folder holds the LocalSystem service executable and the script the tray
///     elevates. A custom install folder (WixUI_InstallDir) can inherit write access for
///     ordinary users, so the MSI must set the folder's permissions explicitly: SYSTEM and
///     Administrators full control, Users read and execute only, inheritance from the parent
///     cut, and the rights inherited by everything installed beneath it.
/// </summary>
public sealed partial class InstallFolderPermissionTests
{
    [Fact]
    public void InstallFolderIsCreatedWithAProtectedDaclThatDoesNotLetUsersWrite()
    {
        string sddl = ReadInstallFolderSddl();

        // Single-line substrings: Administrators own the folder (an owner can always rewrite
        // the DACL) and the DACL is protected from the parent's inherited entries.
        Assert.StartsWith("O:BAD:PAI(", sddl, StringComparison.Ordinal);
        Assert.Contains("O:BA", sddl, StringComparison.Ordinal);
        Assert.Contains("D:P", sddl, StringComparison.Ordinal);
        Dictionary<string, string> rightsByTrustee = ParseAces(sddl);
        Assert.Equal(["BA", "BU", "SY"], rightsByTrustee.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("GA", rightsByTrustee["SY"]);
        Assert.Equal("GA", rightsByTrustee["BA"]);
        Assert.Equal("GRGX", rightsByTrustee["BU"]);
    }

    [Fact]
    public void InstallFolderRightsAreInheritedByContainersAndObjects()
    {
        string sddl = ReadInstallFolderSddl();

        foreach (string ace in AceRegex().Matches(sddl).Select(m => m.Groups[1].Value))
            Assert.Equal("OICI", ace.Split(';')[1]);
    }

    private static string ReadInstallFolderSddl()
    {
        string? wxsPath = InstallerSourceTreeResolver.TryResolveInstallerFile(PackageWxsFileName);
        if (wxsPath == null)
            Assert.Skip(RepositoryMissingSkipReason);
        Assert.NotNull(wxsPath);

        XDocument doc = XDocument.Load(wxsPath);
        XElement? permission = doc.Descendants(smWix + "Component")
                                  .Where(c => (string?) c.Attribute("Directory") == InstallFolderId)
                                  .SelectMany(c => c.Elements(smWix + "CreateFolder"))
                                  .SelectMany(f => f.Elements(smWix + "PermissionEx"))
                                  .FirstOrDefault();

        Assert.NotNull(permission);
        string? sddl = (string?) permission.Attribute("Sddl");
        Assert.False(string.IsNullOrEmpty(sddl));
        return sddl;
    }

    private static Dictionary<string, string> ParseAces(string sddl)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in AceRegex().Matches(sddl))
        {
            // ace-type;ace-flags;rights;object-guid;inherit-object-guid;account-sid
            string[] fields = match.Groups[1].Value.Split(';');
            Assert.Equal("A", fields[0]);
            result[fields[5]] = fields[2];
        }

        return result;
    }

    [GeneratedRegex(@"\(([^)]*)\)")]
    private static partial Regex AceRegex();

    private static readonly XNamespace smWix = "http://wixtoolset.org/schemas/v4/wxs";

    private const string PackageWxsFileName = "Package.wxs";
    private const string InstallFolderId = "MCPFOLDER";
    private const string RepositoryMissingSkipReason = "The repository source tree is not available.";
}
