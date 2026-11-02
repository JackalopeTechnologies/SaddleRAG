// AclInstallLocationGuard.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Reads the Windows permissions on the elevated script, its folder and that folder's parent,
///     and refuses unless only SYSTEM, Administrators and TrustedInstaller can change them.
///     <para>
///         The tray elevates the script by path, so anyone who can replace it (or the folder it
///         sits in, or swap the folder from its parent) gets administrator rights the next time
///         the operator approves the prompt. The installer protects the default location; this
///         check closes the case where the install folder was chosen elsewhere or later weakened.
///     </para>
///     <para>
///         The rules are deliberately simple: any Allow entry to another principal that applies
///         to the object itself and carries one of the listed rights is unsafe. A Deny entry
///         never makes a grant safe. Entries that are inherit-only govern new children, not the
///         object, and are ignored (Program Files carries such a CREATOR OWNER entry).
///     </para>
///     <para>
///         The check is not atomic with the launch. That is acceptable because anyone who could
///         exploit the gap would already hold the rights this check refuses.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AclInstallLocationGuard : IInstallLocationGuard
{
    // SDDL "NT SERVICE\TrustedInstaller": the owner of Program Files and Windows.
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // GENERIC_WRITE and GENERIC_ALL are not FileSystemRights members, but an ACE can carry them
    // unmapped (Program Files' CREATOR OWNER entry does), so they are tested as raw bits.
    private const FileSystemRights GenericWrite = (FileSystemRights) 0x40000000;
    private const FileSystemRights GenericAll = (FileSystemRights) 0x10000000;

    private const FileSystemRights ObjectWriteRights = FileSystemRights.WriteData
                                                       | FileSystemRights.AppendData
                                                       | FileSystemRights.WriteAttributes
                                                       | FileSystemRights.WriteExtendedAttributes
                                                       | FileSystemRights.Delete
                                                       | FileSystemRights.ChangePermissions
                                                       | FileSystemRights.TakeOwnership
                                                       | GenericWrite
                                                       | GenericAll;

    private const FileSystemRights FolderWriteRights = ObjectWriteRights
                                                       | FileSystemRights.DeleteSubdirectoriesAndFiles;

    // Anyone who can delete, rename or take over an ancestor can swap the install folder for another
    // (DeleteSubdirectoriesAndFiles, Delete), or rewrite who may. Creating siblings (AppendData,
    // WriteData) cannot replace the script, so those are not listed: a default C:\ lets
    // Authenticated Users create folders in it.
    private const FileSystemRights AncestorWriteRights = FileSystemRights.DeleteSubdirectoriesAndFiles
                                                         | FileSystemRights.Delete
                                                         | FileSystemRights.ChangePermissions
                                                         | FileSystemRights.TakeOwnership
                                                         | GenericWrite
                                                         | GenericAll;

    private const string UnreadableMessage = "Could not read the permissions of {0}: {1}";
    private const string RemotePathMessage =
        "{0} is on a network share or is a device path; only a local fixed drive can be trusted.";
    private const string NotFixedDriveMessage = "{0} is on a {1} drive; only a local fixed drive can be trusted.";
    private const string MissingMessage = "The {0} ({1}) does not exist.";
    private const string LongPathPrefix = @"\\?\";
    private const char Backslash = '\\';
    private const char DriveColon = ':';
    private const int DriveRootLength = 3;
    private const string NoFolderMessage = "{0} has no containing folder, so it cannot be checked.";
    private const string ReparsePointMessage =
        "{0} ({1}) is a junction or symbolic link, so the permissions read there may not be those of the real location.";
    private const string UntrustedGrantMessage = "{0} ({1}) grants {2} permission to change it.";
    private const string UntrustedOwnerMessage = "{0} ({1}) is owned by {2}, not by SYSTEM or Administrators.";
    private const string ScriptLabel = "script";
    private const string FolderLabel = "install folder";
    private const string AncestorLabel = "folder above the install folder";
    private const string UnknownPrincipal = "an unknown principal";

    private static readonly SecurityIdentifier[] smTrustedPrincipals =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(TrustedInstallerSid)
    ];

    public InstallLocationVerdict Check(string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        InstallLocationVerdict result;
        try
        {
            result = CheckFullPath(Path.GetFullPath(scriptPath));
        }
        catch(Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                      or PlatformNotSupportedException or ArgumentException
                                      or NotSupportedException)
        {
            // Not silent: an unreadable location is reported as unsafe, with the reason. The
            // tray must never elevate something whose protection it could not confirm.
            result = InstallLocationVerdict.Unsafe(string.Format(UnreadableMessage, scriptPath, ex.Message));
        }

        return result;
    }

    /// <summary>
    ///     The first principal outside SYSTEM, Administrators and TrustedInstaller that holds an
    ///     Allow entry applying to the object itself and carrying a right that is dangerous for
    ///     <paramref name="scope" />; null when there is none.
    /// </summary>
    internal static SecurityIdentifier? FindUntrustedGrant(IEnumerable<AclEntry> entries,
                                                           InstallLocationScope scope)
    {
        ArgumentNullException.ThrowIfNull(entries);

        SecurityIdentifier? result = null;
        foreach (AclEntry entry in entries)
        {
            bool appliesToObject = entry.Type == AccessControlType.Allow
                                   && !entry.Propagation.HasFlag(PropagationFlags.InheritOnly);
            if (result is null
                && appliesToObject
                && !smTrustedPrincipals.Contains(entry.Sid)
                && GrantsDangerousRight(entry.Rights, scope))
                result = entry.Sid;
        }

        return result;
    }

    internal static bool IsTrustedOwner(SecurityIdentifier? owner) =>
        owner is not null && smTrustedPrincipals.Contains(owner);

    private static bool GrantsDangerousRight(FileSystemRights rights, InstallLocationScope scope)
    {
        bool result = scope switch
                      {
                          InstallLocationScope.Script => (rights & ObjectWriteRights) != 0,
                          InstallLocationScope.Folder => (rights & FolderWriteRights) != 0,
                          // Modify and FullControl are tested as whole masks: Modify's individual
                          // bits (create files, write attributes) appear in harmless entries too.
                          _ => (rights & AncestorWriteRights) != 0
                               || (rights & FileSystemRights.Modify) == FileSystemRights.Modify
                               || (rights & FileSystemRights.FullControl) == FileSystemRights.FullControl
                      };
        return result;
    }

    /// <summary>
    ///     Refuses any path that is not a local drive path. On a network share the trusted
    ///     well-known principals would be the file server's, not this computer's, and the file
    ///     crosses the network on its way here. This is the string-only part: a UNC path
    ///     (<c>\\server\share</c>), a <c>\\?\UNC\</c> path and every other device path are refused;
    ///     <c>\\?\C:\</c> is the same local drive as <c>C:\</c> and is accepted.
    /// </summary>
    internal static InstallLocationVerdict CheckPathForm(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        string drivePath = StripLongPathPrefix(fullPath);
        bool isDrivePath = drivePath.Length >= DriveRootLength
                           && char.IsAsciiLetter(drivePath[0])
                           && drivePath[1] == DriveColon
                           && drivePath[2] == Backslash;

        return isDrivePath
                   ? InstallLocationVerdict.Safe
                   : InstallLocationVerdict.Unsafe(string.Format(RemotePathMessage, fullPath));
    }

    private static string StripLongPathPrefix(string fullPath) =>
        fullPath.StartsWith(LongPathPrefix, StringComparison.Ordinal) ? fullPath[LongPathPrefix.Length..] : fullPath;

    private static InstallLocationVerdict CheckVolume(string fullPath)
    {
        InstallLocationVerdict result = CheckPathForm(fullPath);
        if (result.IsSafe)
        {
            DriveType driveType = new DriveInfo(StripLongPathPrefix(fullPath)[..DriveRootLength]).DriveType;
            if (driveType != DriveType.Fixed)
                result = InstallLocationVerdict.Unsafe(string.Format(NotFixedDriveMessage, fullPath, driveType));
        }

        return result;
    }

    private static InstallLocationVerdict CheckFullPath(string fullPath)
    {
        // Before any reparse or ACL read: those reads would otherwise be trusted on a volume
        // whose "SYSTEM" and "Administrators" are not this computer's.
        InstallLocationVerdict result = CheckVolume(fullPath);
        if (result.IsSafe)
            result = CheckPermissions(fullPath);

        return result;
    }

    private static InstallLocationVerdict CheckPermissions(string scriptPath)
    {
        FileInfo script = new(scriptPath);
        DirectoryInfo? folder = script.Directory;

        InstallLocationVerdict result;
        if (folder is null)
        {
            result = InstallLocationVerdict.Unsafe(string.Format(NoFolderMessage, scriptPath));
        }
        else
        {
            // Every folder from the install folder up to the volume root: whoever can delete or
            // take over any of them can substitute the install folder.
            List<DirectoryInfo> ancestors = [];
            for (DirectoryInfo? ancestor = folder.Parent; ancestor is not null; ancestor = ancestor.Parent)
                ancestors.Add(ancestor);

            result = CheckForReparsePoints(script, folder, ancestors);
            if (result.IsSafe)
                result = CheckObject(scriptPath, ScriptLabel, script.GetAccessControl(), InstallLocationScope.Script);
            if (result.IsSafe)
                result = CheckObject(folder.FullName, FolderLabel, folder.GetAccessControl(), InstallLocationScope.Folder);
            foreach (DirectoryInfo ancestor in ancestors)
            {
                if (result.IsSafe)
                    result = CheckObject(ancestor.FullName, AncestorLabel, ancestor.GetAccessControl(),
                                         InstallLocationScope.Ancestor);
            }
        }

        return result;
    }

    // A junction or symbolic link anywhere on the path means the ACL read for that step is the
    // link's own, while the launch follows it to a target whose permissions were never checked.
    private static InstallLocationVerdict CheckForReparsePoints(FileInfo script,
                                                                DirectoryInfo folder,
                                                                IEnumerable<DirectoryInfo> ancestors)
    {
        (FileSystemInfo Info, string Label)[] chain =
        [
            (script, ScriptLabel),
            (folder, FolderLabel),
            .. ancestors.Select(ancestor => ((FileSystemInfo) ancestor, AncestorLabel))
        ];

        InstallLocationVerdict result = InstallLocationVerdict.Safe;
        foreach ((FileSystemInfo info, string label) in chain)
        {
            // Exists first: the attributes of a missing path read as all bits set, which would
            // wrongly include the reparse-point bit.
            if (result.IsSafe && !info.Exists)
                result = InstallLocationVerdict.Unsafe(string.Format(MissingMessage, label, info.FullName));
            if (result.IsSafe && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                result = InstallLocationVerdict.Unsafe(string.Format(ReparsePointMessage, label, info.FullName));
        }

        return result;
    }

    private static InstallLocationVerdict CheckObject(string path,
                                                      string label,
                                                      CommonObjectSecurity security,
                                                      InstallLocationScope scope)
    {
        IEnumerable<AclEntry> entries = security
                                        .GetAccessRules(includeExplicit: true,
                                                        includeInherited: true,
                                                        typeof(SecurityIdentifier))
                                        .OfType<FileSystemAccessRule>()
                                        .Where(rule => rule.IdentityReference is SecurityIdentifier)
                                        .Select(rule => new AclEntry((SecurityIdentifier) rule.IdentityReference,
                                                                     rule.FileSystemRights,
                                                                     rule.AccessControlType,
                                                                     rule.PropagationFlags));
        SecurityIdentifier? offender = FindUntrustedGrant(entries, scope);
        SecurityIdentifier? owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        InstallLocationVerdict result = InstallLocationVerdict.Safe;
        if (offender is not null)
            result = InstallLocationVerdict.Unsafe(string.Format(UntrustedGrantMessage, label, path, Describe(offender)));
        if (result.IsSafe && !IsTrustedOwner(owner))
            result = InstallLocationVerdict.Unsafe(string.Format(UntrustedOwnerMessage, label, path, Describe(owner)));

        return result;
    }

    private static string Describe(SecurityIdentifier? sid)
    {
        string result = UnknownPrincipal;
        if (sid is not null)
        {
            try
            {
                result = sid.Translate(typeof(NTAccount)).Value;
            }
            catch(SystemException)
            {
                // A SID with no account name (deleted user, other domain) is still named by its
                // SID string; the name is only there to help the operator read the log.
                result = sid.Value;
            }
        }

        return result;
    }
}
