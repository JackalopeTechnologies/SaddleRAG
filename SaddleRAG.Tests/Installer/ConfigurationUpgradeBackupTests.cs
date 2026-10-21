// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SaddleRAG.Installer.Helper;

namespace SaddleRAG.Tests.Installer;

public sealed class ConfigurationUpgradeBackupTests : IDisposable
{
    public ConfigurationUpgradeBackupTests()
    {
        mRoot = Path.Combine(Path.GetTempPath(), $"saddlerag-config-test-{Guid.NewGuid():N}");
        mInstall = Path.Combine(mRoot, "application");
        mBackup = Path.Combine(mRoot, $"SaddleRAG-Upgrade-{Guid.NewGuid():B}");
        Directory.CreateDirectory(mInstall);
    }

    [Fact]
    public void UpgradeAndRollbackRestoreOriginalBytesUntilCommit()
    {
        byte[] settings = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("{\"EmbeddingModel\":\"Google\"}\r\n")];
        byte[] overrides = Encoding.UTF8.GetBytes("{\"Sorter\":\"Onnx\"}");
        File.WriteAllBytes(SettingsPath, settings);
        File.WriteAllBytes(OverridesPath, overrides);
        var backup = CreateBackup();
        backup.Snapshot();

        File.Delete(SettingsPath);
        File.Delete(OverridesPath);
        File.WriteAllText(SettingsPath, "new package defaults");
        backup.Restore();
        Assert.Equal(settings, File.ReadAllBytes(SettingsPath));
        Assert.Equal(overrides, File.ReadAllBytes(OverridesPath));

        File.WriteAllText(SettingsPath, "changes made by a failed installer action");
        backup.Restore();
        Assert.Equal(settings, File.ReadAllBytes(SettingsPath));
        Assert.True(Directory.Exists(mBackup));
        backup.Complete();
        Assert.False(Directory.Exists(mBackup));
    }

    [Fact]
    public void MissingOriginalSettingsLeaveNewDefaultsInPlace()
    {
        var backup = CreateBackup();
        backup.Snapshot();
        File.WriteAllText(SettingsPath, "fresh default");
        backup.Restore();
        Assert.Equal("fresh default", File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(OverridesPath));
    }

    [Fact]
    public void ExistingSnapshotCannotBeOverwrittenOnRetry()
    {
        File.WriteAllText(SettingsPath, "operator setting");
        var backup = CreateBackup();
        backup.Snapshot();
        File.WriteAllText(SettingsPath, "changed setting");
        Assert.Throws<IOException>(backup.Snapshot);
        backup.Restore();
        Assert.Equal("operator setting", File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrCorruptSnapshotFailsBeforeChangingInstalledFiles(bool corrupt)
    {
        File.WriteAllText(SettingsPath, "operator setting");
        if (corrupt)
        {
            Directory.CreateDirectory(mBackup);
            File.WriteAllText(Path.Combine(mBackup, "configuration.json"), "{");
        }
        Assert.ThrowsAny<Exception>(() => CreateBackup().Restore());
        Assert.Equal("operator setting", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void SnapshotCannotRestoreAnotherInstallationOrWriteUnownedFiles()
    {
        File.WriteAllText(SettingsPath, "operator setting");
        var backup = CreateBackup();
        backup.Snapshot();
        var other = Path.Combine(mRoot, "another-application");
        Assert.Throws<InvalidDataException>(() => new ConfigurationUpgradeBackup(other, mBackup).Restore());

        var unexpected = new { InstallDirectory = mInstall, Files = new Dictionary<string, byte[]>
            { ["appsettings.json"] = Encoding.UTF8.GetBytes("replacement"), ["../unexpected"] = [] } };
        File.WriteAllText(Path.Combine(mBackup, "configuration.json"), JsonSerializer.Serialize(unexpected));
        Assert.Throws<InvalidDataException>(backup.Restore);
        Assert.Equal("operator setting", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void BackupMustBeOwnedAndOutsideTheInstallation()
    {
        Assert.Throws<ArgumentException>(() => new ConfigurationUpgradeBackup(mInstall, Path.Combine(mRoot, "unowned")));
        Assert.Throws<ArgumentException>(() => new ConfigurationUpgradeBackup(mInstall,
            Path.Combine(mInstall, $"SaddleRAG-Upgrade-{Guid.NewGuid():B}")));
    }

    [Fact]
    public void CommitDoesNotRecursivelyRemoveUnexpectedFiles()
    {
        var backup = CreateBackup();
        backup.Snapshot();
        string unrelated = Path.Combine(mBackup, "unexpected.txt");
        File.WriteAllText(unrelated, "retain");
        Assert.Throws<IOException>(backup.Complete);
        Assert.Equal("retain", File.ReadAllText(unrelated));
    }

    [Fact]
    public void PackagePreservesConfigurationBeforeRemovalAndRestoresBeforePatching()
    {
        string? path = InstallerSourceTreeResolver.TryResolveInstallerFile("Package.wxs");
        Assert.NotNull(path);
        XDocument package = XDocument.Load(path);
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        Assert.Equal("afterInstallInitialize", (string?)package.Descendants(ns + "MajorUpgrade").Single().Attribute("Schedule"));
        var sequence = package.Descendants(ns + "InstallExecuteSequence").Single();
        XElement Step(string name) => sequence.Elements(ns + "Custom").Single(e => (string?)e.Attribute("Action") == name);
        Assert.Equal("InstallInitialize", (string?)Step("SnapshotConfiguration").Attribute("Before"));
        Assert.Equal("InstallFiles", (string?)Step("RestoreConfiguration").Attribute("After"));
        Assert.Equal("RestoreConfiguration", (string?)Step("CompleteConfiguration").Attribute("After"));
        Assert.Equal("CompleteConfiguration", (string?)Step("EscapeAppSettingsProperties").Attribute("After"));
        var rollback = package.Descendants(ns + "CustomAction").Single(e => (string?)e.Attribute("Id") == "RollbackConfiguration");
        Assert.Equal("rollback", (string?)rollback.Attribute("Execute"));
        Assert.Equal("no", (string?)rollback.Attribute("Impersonate"));
    }

    public void Dispose()
    {
        string expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(mRoot), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(mRoot).StartsWith("saddlerag-config-test-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture cleanup path.");
        Directory.Delete(mRoot, recursive: true);
    }

    private ConfigurationUpgradeBackup CreateBackup() => new(mInstall, mBackup);
    private string SettingsPath => Path.Combine(mInstall, "appsettings.json");
    private string OverridesPath => Path.Combine(mInstall, "runtime-overrides.json");
    private readonly string mRoot;
    private readonly string mInstall;
    private readonly string mBackup;
}
