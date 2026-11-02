// AclInstallLocationGuardTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

/// <summary>
///     The rule tests build <see cref="AclEntry" /> values in memory and read no
///     ACL, so they cannot be affected by the machine's folders. The two real-disk tests only
///     read ACLs. All of them skip off Windows, where the ACL types do not exist.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AclInstallLocationGuardTests
{
    private const string WindowsOnlySkipReason = "ACL checks apply only to Windows.";
    private const FileSystemRights GenericWrite = (FileSystemRights) 0x40000000;
    private const FileSystemRights GenericAll = (FileSystemRights) 0x10000000;

    private static readonly SecurityIdentifier smUsers = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier smEveryone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier smCreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier smSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier smAdministrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier smTrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private static AclEntry Allow(SecurityIdentifier sid,
                                  FileSystemRights rights,
                                  PropagationFlags propagation = PropagationFlags.None) =>
        new(sid, rights, AccessControlType.Allow, propagation);

    private static AclEntry Deny(SecurityIdentifier sid, FileSystemRights rights) =>
        new(sid, rights, AccessControlType.Deny, PropagationFlags.None);

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);
    }

    [Theory]
    [InlineData(InstallLocationScope.Script)]
    [InlineData(InstallLocationScope.Folder)]
    [InlineData(InstallLocationScope.Parent)]
    public void ReadAndExecuteForUsersIsHarmless(InstallLocationScope scope)
    {
        RequireWindows();

        SecurityIdentifier? offender = AclInstallLocationGuard.FindUntrustedGrant(
            [Allow(smUsers, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)], scope);

        Assert.Null(offender);
    }

    [Theory]
    [InlineData(InstallLocationScope.Script)]
    [InlineData(InstallLocationScope.Folder)]
    [InlineData(InstallLocationScope.Parent)]
    public void TrustedPrincipalsMayHoldFullControl(InstallLocationScope scope)
    {
        RequireWindows();

        AclEntry[] rules =
        [
            Allow(smSystem, FileSystemRights.FullControl),
            Allow(smAdministrators, FileSystemRights.FullControl),
            Allow(smTrustedInstaller, FileSystemRights.FullControl)
        ];

        Assert.Null(AclInstallLocationGuard.FindUntrustedGrant(rules, scope));
    }

    [Theory]
    [InlineData(InstallLocationScope.Script, FileSystemRights.WriteData)]
    [InlineData(InstallLocationScope.Script, FileSystemRights.AppendData)]
    [InlineData(InstallLocationScope.Script, FileSystemRights.WriteAttributes)]
    [InlineData(InstallLocationScope.Script, FileSystemRights.WriteExtendedAttributes)]
    [InlineData(InstallLocationScope.Script, FileSystemRights.Delete)]
    [InlineData(InstallLocationScope.Script, FileSystemRights.ChangePermissions)]
    [InlineData(InstallLocationScope.Script, FileSystemRights.TakeOwnership)]
    [InlineData(InstallLocationScope.Script, FileSystemRights.Modify)]
    [InlineData(InstallLocationScope.Script, FileSystemRights.FullControl)]
    [InlineData(InstallLocationScope.Script, GenericWrite)]
    [InlineData(InstallLocationScope.Script, GenericAll)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.WriteData)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.AppendData)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.WriteAttributes)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.WriteExtendedAttributes)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.Delete)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.ChangePermissions)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.TakeOwnership)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.Modify)]
    [InlineData(InstallLocationScope.Folder, FileSystemRights.FullControl)]
    [InlineData(InstallLocationScope.Folder, GenericWrite)]
    [InlineData(InstallLocationScope.Folder, GenericAll)]
    [InlineData(InstallLocationScope.Parent, FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(InstallLocationScope.Parent, FileSystemRights.Modify)]
    [InlineData(InstallLocationScope.Parent, FileSystemRights.FullControl)]
    [InlineData(InstallLocationScope.Parent, GenericWrite)]
    [InlineData(InstallLocationScope.Parent, GenericAll)]
    public void AnUntrustedWriteRightIsReported(InstallLocationScope scope, FileSystemRights rights)
    {
        RequireWindows();

        SecurityIdentifier? offender = AclInstallLocationGuard.FindUntrustedGrant([Allow(smUsers, rights)], scope);

        Assert.Equal(smUsers, offender);
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void AParentMayLetUsersCreateAndNothingWorseThanThat(FileSystemRights rights)
    {
        RequireWindows();

        // Only a parent grant that could move or delete the install folder matters; creating
        // siblings, or touching the parent itself, cannot replace the script.
        Assert.Null(AclInstallLocationGuard.FindUntrustedGrant([Allow(smUsers, rights)], InstallLocationScope.Parent));
    }

    [Theory]
    [InlineData(InstallLocationScope.Script)]
    [InlineData(InstallLocationScope.Folder)]
    [InlineData(InstallLocationScope.Parent)]
    public void AnInheritOnlyEntryDoesNotApplyToTheObjectItself(InstallLocationScope scope)
    {
        RequireWindows();

        // Program Files carries "CREATOR OWNER: full control, inherit only" -- it governs new
        // children, not Program Files.
        AclEntry rule = Allow(smCreatorOwner, GenericAll, PropagationFlags.InheritOnly);

        Assert.Null(AclInstallLocationGuard.FindUntrustedGrant([rule], scope));
    }

    [Fact]
    public void CreatorOwnerThatAppliesToTheParentItselfIsUntrusted()
    {
        RequireWindows();

        AclEntry rule = Allow(smCreatorOwner, GenericAll);

        Assert.Equal(smCreatorOwner,
                     AclInstallLocationGuard.FindUntrustedGrant([rule], InstallLocationScope.Parent));
    }

    [Fact]
    public void ADenyEntryNeverMakesAGrantSafe()
    {
        RequireWindows();

        AclEntry[] rules =
        [
            Deny(smUsers, FileSystemRights.FullControl),
            Allow(smEveryone, FileSystemRights.WriteData)
        ];

        Assert.Equal(smEveryone, AclInstallLocationGuard.FindUntrustedGrant(rules, InstallLocationScope.Script));
    }

    [Fact]
    public void ADenyEntryAloneIsNotAGrant()
    {
        RequireWindows();

        AclEntry deny = Deny(smUsers, FileSystemRights.FullControl);

        Assert.Null(AclInstallLocationGuard.FindUntrustedGrant([deny], InstallLocationScope.Script));
    }

    [Fact]
    public void OnlySystemAdministratorsAndTrustedInstallerMayOwn()
    {
        RequireWindows();

        Assert.True(AclInstallLocationGuard.IsTrustedOwner(smSystem));
        Assert.True(AclInstallLocationGuard.IsTrustedOwner(smAdministrators));
        Assert.True(AclInstallLocationGuard.IsTrustedOwner(smTrustedInstaller));
        Assert.False(AclInstallLocationGuard.IsTrustedOwner(smUsers));
        Assert.False(AclInstallLocationGuard.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
        Assert.False(AclInstallLocationGuard.IsTrustedOwner(owner: null));
    }

    [Fact]
    public void AFolderTheCurrentUserCreatedIsUnsafe()
    {
        RequireWindows();

        string folder = Path.Combine(Path.GetTempPath(), $"saddlerag-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            string script = Path.Combine(folder, "Restart-SaddleRAGComponent.ps1");
            File.WriteAllText(script, "# stand-in for the elevated script");

            InstallLocationVerdict verdict = new AclInstallLocationGuard().Check(script);

            Assert.False(verdict.IsSafe);
            Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AScriptThatCannotBeReadIsUnsafe()
    {
        RequireWindows();

        string missing = Path.Combine(Path.GetTempPath(), $"saddlerag-guard-{Guid.NewGuid():N}", "absent.ps1");

        InstallLocationVerdict verdict = new AclInstallLocationGuard().Check(missing);

        Assert.False(verdict.IsSafe);
        Assert.Contains(missing, verdict.Reason);
    }

    [Fact]
    public void AnAdministratorOnlyLocationIsSafe()
    {
        RequireWindows();

        // This machine's own Windows PowerShell: a file in an administrator-only folder
        // (System32\WindowsPowerShell\v1.0) whose parent is administrator-only too, the same
        // shape as a script in C:\Program Files\SaddleRAG.Mcp. Program Files itself cannot be
        // the folder here because it holds no file that every machine is guaranteed to have.
        string script = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                     "WindowsPowerShell",
                                     "v1.0",
                                     "powershell.exe");
        if (!File.Exists(script))
            Assert.Skip("Windows PowerShell is not present on this machine.");

        InstallLocationVerdict verdict = new AclInstallLocationGuard().Check(script);

        Assert.True(verdict.IsSafe, verdict.Reason);
    }
}
