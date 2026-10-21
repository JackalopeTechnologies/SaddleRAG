// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace SaddleRAG.Installer.Helper;

/// <summary>Preserves operator configuration across MSI's removal of the old product.</summary>
public sealed class ConfigurationUpgradeBackup
{
    public ConfigurationUpgradeBackup(string installDirectory, string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        mInstallDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
        mBackupDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupDirectory));
        var name = Path.GetFileName(mBackupDirectory);
        if (!name.StartsWith(BackupPrefix, StringComparison.Ordinal) || !Guid.TryParse(name[BackupPrefix.Length..], out _))
            throw new ArgumentException("The backup directory must have an installer transaction identity.", nameof(backupDirectory));
        if (mBackupDirectory.StartsWith(mInstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            mInstallDirectory.StartsWith(mBackupDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            mInstallDirectory.Equals(mBackupDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Configuration backup and installation directories must be separate.", nameof(backupDirectory));
        RejectReparsePoints(mInstallDirectory);
        RejectReparsePoints(mBackupDirectory);
    }

    /// <summary>Copies the two owned configuration files before any uninstall action can run.</summary>
    public void Snapshot()
    {
        if (Directory.Exists(mBackupDirectory))
            throw new IOException("The configuration backup directory already exists; retain it for recovery.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in smFileNames)
        {
            var path = Path.Combine(mInstallDirectory, name);
            RejectReparsePoints(path);
            if (File.Exists(path))
                files.Add(name, File.ReadAllBytes(path));
        }

        CreatePrivateDirectory();
        var snapshot = new ConfigurationSnapshot(mInstallDirectory, files);
        File.WriteAllText(Path.Combine(mBackupDirectory, SnapshotFileName), JsonSerializer.Serialize(snapshot));
    }

    /// <summary>Restores original bytes before configuration patching and service startup, or on rollback.</summary>
    public void Restore()
    {
        var snapshot = ReadSnapshot();
        // Validate every target before writing either configuration file.
        foreach (string name in snapshot.Files.Keys)
        {
            if (!smFileNames.Contains(name, StringComparer.Ordinal) || snapshot.Files[name] == null)
                throw new InvalidDataException("The configuration snapshot contains an unexpected file.");
            RejectReparsePoints(Path.Combine(mInstallDirectory, name));
        }

        Directory.CreateDirectory(mInstallDirectory);
        foreach ((string name, byte[] bytes) in snapshot.Files)
        {
            var target = Path.Combine(mInstallDirectory, name);
            var temporary = target + TemporarySuffix;
            RejectReparsePoints(temporary);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
        }
    }

    /// <summary>Removes only the owned snapshot after a successful transaction.</summary>
    public void Complete()
    {
        _ = ReadSnapshot();
        File.Delete(Path.Combine(mBackupDirectory, SnapshotFileName));
        Directory.Delete(mBackupDirectory, recursive: false);
    }

    private ConfigurationSnapshot ReadSnapshot()
    {
        var path = Path.Combine(mBackupDirectory, SnapshotFileName);
        RejectReparsePoints(path);
        var snapshot = JsonSerializer.Deserialize<ConfigurationSnapshot>(File.ReadAllText(path))
                       ?? throw new InvalidDataException("The configuration snapshot is empty.");
        if (!mInstallDirectory.Equals(snapshot.InstallDirectory, StringComparison.OrdinalIgnoreCase) || snapshot.Files == null)
            throw new InvalidDataException("The configuration snapshot belongs to a different installation.");
        return snapshot;
    }

    private void CreatePrivateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new InvalidOperationException("The installer user has no security identity.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            SecurityIdentifier[] principals =
            [
                owner,
                new(WellKnownSidType.LocalSystemSid, null),
                new(WellKnownSidType.BuiltinAdministratorsSid, null)
            ];
            foreach (SecurityIdentifier principal in principals)
                security.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(mBackupDirectory).Create(security);
        }
        else
            Directory.CreateDirectory(mBackupDirectory);
    }

    private static void RejectReparsePoints(string path)
    {
        var entry = new FileInfo(path);
        if (entry.LinkTarget != null)
            throw new IOException("Configuration preservation does not follow symbolic links.");
        for (DirectoryInfo? directory = new(path); directory != null; directory = directory.Parent)
        {
            if (directory.LinkTarget != null || directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Configuration preservation does not follow reparse points.");
        }
    }

    private readonly string mInstallDirectory;
    private readonly string mBackupDirectory;
    private const string BackupPrefix = "SaddleRAG-Upgrade-";
    private const string SnapshotFileName = "configuration.json";
    private const string TemporarySuffix = ".upgrade-tmp";
    private const string SettingsFileName = "appsettings.json";
    private const string OverridesFileName = "runtime-overrides.json";
    private static readonly string[] smFileNames = [SettingsFileName, OverridesFileName];
}
