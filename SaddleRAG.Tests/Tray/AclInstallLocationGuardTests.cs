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

    // Built on use, not in static fields: constructing a SecurityIdentifier throws off Windows,
    // and a throwing type initializer would fail these tests before RequireWindows could skip them.
    private static SecurityIdentifier UsersSid => new(WellKnownSidType.BuiltinUsersSid, null);
    private static SecurityIdentifier EveryoneSid => new(WellKnownSidType.WorldSid, null);
    private static SecurityIdentifier CreatorOwnerSid => new(WellKnownSidType.CreatorOwnerSid, null);
    private static SecurityIdentifier SystemSid => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier AdministratorsSid => new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static SecurityIdentifier AuthenticatedUsersSid => new(WellKnownSidType.AuthenticatedUserSid, null);

    private static SecurityIdentifier TrustedInstallerSid =>
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
    [InlineData(InstallLocationScope.Ancestor)]
    public void ReadAndExecuteForUsersIsHarmless(InstallLocationScope scope)
    {
        RequireWindows();

        SecurityIdentifier? offender = AclInstallLocationGuard.FindUntrustedGrant(
            [Allow(UsersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)], scope);

        Assert.Null(offender);
    }

    [Theory]
    [InlineData(InstallLocationScope.Script)]
    [InlineData(InstallLocationScope.Folder)]
    [InlineData(InstallLocationScope.Ancestor)]
    public void TrustedPrincipalsMayHoldFullControl(InstallLocationScope scope)
    {
        RequireWindows();

        AclEntry[] rules =
        [
            Allow(SystemSid, FileSystemRights.FullControl),
            Allow(AdministratorsSid, FileSystemRights.FullControl),
            Allow(TrustedInstallerSid, FileSystemRights.FullControl)
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
    [InlineData(InstallLocationScope.Ancestor, FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(InstallLocationScope.Ancestor, FileSystemRights.Delete)]
    [InlineData(InstallLocationScope.Ancestor, FileSystemRights.ChangePermissions)]
    [InlineData(InstallLocationScope.Ancestor, FileSystemRights.TakeOwnership)]
    [InlineData(InstallLocationScope.Ancestor, FileSystemRights.Modify)]
    [InlineData(InstallLocationScope.Ancestor, FileSystemRights.FullControl)]
    [InlineData(InstallLocationScope.Ancestor, GenericWrite)]
    [InlineData(InstallLocationScope.Ancestor, GenericAll)]
    public void AnUntrustedWriteRightIsReported(InstallLocationScope scope, FileSystemRights rights)
    {
        RequireWindows();

        SecurityIdentifier? offender = AclInstallLocationGuard.FindUntrustedGrant([Allow(UsersSid, rights)], scope);

        Assert.Equal(UsersSid, offender);
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    public void AnAncestorMayLetUsersCreateItemsAndNothingWorseThanThat(FileSystemRights rights)
    {
        RequireWindows();

        // Only a grant that could delete, rename or take over an ancestor matters; creating
        // siblings in it cannot replace the install folder or the script.
        Assert.Null(AclInstallLocationGuard.FindUntrustedGrant([Allow(UsersSid, rights)], InstallLocationScope.Ancestor));
    }

    [Theory]
    [InlineData(InstallLocationScope.Script)]
    [InlineData(InstallLocationScope.Folder)]
    [InlineData(InstallLocationScope.Ancestor)]
    public void AnInheritOnlyEntryDoesNotApplyToTheObjectItself(InstallLocationScope scope)
    {
        RequireWindows();

        // Program Files carries "CREATOR OWNER: full control, inherit only" -- it governs new
        // children, not Program Files.
        AclEntry rule = Allow(CreatorOwnerSid, GenericAll, PropagationFlags.InheritOnly);

        Assert.Null(AclInstallLocationGuard.FindUntrustedGrant([rule], scope));
    }

    [Fact]
    public void CreatorOwnerThatAppliesToTheParentItselfIsUntrusted()
    {
        RequireWindows();

        AclEntry rule = Allow(CreatorOwnerSid, GenericAll);

        Assert.Equal(CreatorOwnerSid,
                     AclInstallLocationGuard.FindUntrustedGrant([rule], InstallLocationScope.Ancestor));
    }

    [Fact]
    public void ADenyEntryNeverMakesAGrantSafe()
    {
        RequireWindows();

        AclEntry[] rules =
        [
            Deny(UsersSid, FileSystemRights.FullControl),
            Allow(EveryoneSid, FileSystemRights.WriteData)
        ];

        Assert.Equal(EveryoneSid, AclInstallLocationGuard.FindUntrustedGrant(rules, InstallLocationScope.Script));
    }

    [Fact]
    public void ADenyEntryAloneIsNotAGrant()
    {
        RequireWindows();

        AclEntry deny = Deny(UsersSid, FileSystemRights.FullControl);

        Assert.Null(AclInstallLocationGuard.FindUntrustedGrant([deny], InstallLocationScope.Script));
    }

    [Fact]
    public void OnlySystemAdministratorsAndTrustedInstallerMayOwn()
    {
        RequireWindows();

        Assert.True(AclInstallLocationGuard.IsTrustedOwner(SystemSid));
        Assert.True(AclInstallLocationGuard.IsTrustedOwner(AdministratorsSid));
        Assert.True(AclInstallLocationGuard.IsTrustedOwner(TrustedInstallerSid));
        Assert.False(AclInstallLocationGuard.IsTrustedOwner(UsersSid));
        Assert.False(AclInstallLocationGuard.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
        Assert.False(AclInstallLocationGuard.IsTrustedOwner(owner: null));
    }

    [Fact]
    public void ADefaultVolumeRootPassesTheAncestorRule()
    {
        RequireWindows();

        // Modelled on a default C:\ : Authenticated Users may create folders in the root itself,
        // and their Modify (generic bits, 0xE0010000 as read from disk) is inherit-only.
        AclEntry[] rules =
        [
            Allow(AuthenticatedUsersSid, FileSystemRights.CreateDirectories),
            Allow(AuthenticatedUsersSid, (FileSystemRights) unchecked((int) 0xE0010000), PropagationFlags.InheritOnly),
            Allow(SystemSid, FileSystemRights.FullControl),
            Allow(AdministratorsSid, FileSystemRights.FullControl),
            Allow(UsersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)
        ];

        Assert.Null(AclInstallLocationGuard.FindUntrustedGrant(rules, InstallLocationScope.Ancestor));
    }

    [Fact]
    public void ADefaultProgramFilesPassesTheAncestorRule()
    {
        RequireWindows();

        AclEntry[] rules =
        [
            Allow(CreatorOwnerSid, GenericAll, PropagationFlags.InheritOnly),
            Allow(SystemSid, FileSystemRights.Modify | FileSystemRights.Synchronize),
            Allow(AdministratorsSid, FileSystemRights.Modify | FileSystemRights.Synchronize),
            Allow(UsersSid, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize),
            Allow(UsersSid, (FileSystemRights) unchecked((int) 0xA0000000), PropagationFlags.InheritOnly),
            Allow(TrustedInstallerSid, FileSystemRights.FullControl)
        ];

        Assert.Null(AclInstallLocationGuard.FindUntrustedGrant(rules, InstallLocationScope.Ancestor));
    }

    [Fact]
    public void AScriptReachedThroughAJunctionIsUnsafe()
    {
        RequireWindows();

        string root = Path.Combine(Path.GetTempPath(), $"saddlerag-junction-{Guid.NewGuid():N}");
        string realFolder = Path.Combine(root, "real");
        string link = Path.Combine(root, "link");
        Directory.CreateDirectory(realFolder);
        try
        {
            File.WriteAllText(Path.Combine(realFolder, "Restart-SaddleRAGComponent.ps1"), "# stand-in");
            CreateJunction(link, realFolder);

            InstallLocationVerdict verdict =
                new AclInstallLocationGuard().Check(Path.Combine(link, "Restart-SaddleRAGComponent.ps1"));

            Assert.False(verdict.IsSafe);
            Assert.Contains(link, verdict.Reason);
            Assert.Contains("junction", verdict.Reason);
        }
        finally
        {
            // Remove the junction itself first: deleting the root recursively must not follow it.
            if (Directory.Exists(link))
                Directory.Delete(link, recursive: false);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AJunctionHigherUpThePathIsUnsafe()
    {
        RequireWindows();

        string root = Path.Combine(Path.GetTempPath(), $"saddlerag-junction-{Guid.NewGuid():N}");
        string realParent = Path.Combine(root, "real");
        string link = Path.Combine(root, "link");
        Directory.CreateDirectory(Path.Combine(realParent, "install"));
        try
        {
            File.WriteAllText(Path.Combine(realParent, "install", "Restart-SaddleRAGComponent.ps1"), "# stand-in");
            CreateJunction(link, realParent);

            InstallLocationVerdict verdict = new AclInstallLocationGuard()
                .Check(Path.Combine(link, "install", "Restart-SaddleRAGComponent.ps1"));

            Assert.False(verdict.IsSafe);
            Assert.Contains(link, verdict.Reason);
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link, recursive: false);
            Directory.Delete(root, recursive: true);
        }
    }

    // mklink /J needs no elevation, unlike Directory.CreateSymbolicLink.
    private static void CreateJunction(string link, string target)
    {
        System.Diagnostics.ProcessStartInfo info = new("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                                                   {
                                                       CreateNoWindow = true,
                                                       UseShellExecute = false,
                                                       RedirectStandardOutput = true
                                                   };
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(expected: 0, process.ExitCode);
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
