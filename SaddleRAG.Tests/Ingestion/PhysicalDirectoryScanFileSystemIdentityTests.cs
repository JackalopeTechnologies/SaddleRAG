// PhysicalDirectoryScanFileSystemIdentityTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text;
using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

public sealed class PhysicalDirectoryScanFileSystemIdentityTests
{
    [Fact]
    public async Task SameSizeAndTimestampReplacementDoesNotMatchDiscoveredFileIdentity()
    {
        using var fixture = new PhysicalIdentityFixture();
        string filePath = Path.Combine(fixture.RootPath, "manual.txt");
        File.WriteAllText(filePath, "first", Encoding.UTF8);
        var fileSystem = new PhysicalDirectoryScanFileSystem();
        DirectoryEntrySnapshot root = Inspect(fileSystem, fixture.RootPath);
        DirectoryEntrySnapshot discovered = Assert.Single(
            fileSystem.EnumerateDirectory(fixture.RootPath, root).Entries);
        string displacedPath = Path.Combine(fixture.ContainerPath, "displaced-manual.txt");
        File.Move(filePath, displacedPath);
        File.WriteAllText(filePath, "other", Encoding.UTF8);
        File.SetLastWriteTimeUtc(filePath, discovered.LastWriteTimeUtc);

        StableFileReadResult result = await fileSystem.ReadStableFileAsync(
                                          filePath,
                                          maxFileBytes: 1024,
                                          cancellationToken: TestContext.Current.CancellationToken,
                                          expectedSnapshot: discovered);

        Assert.False(result.Succeeded);
        Assert.Equal(DirectoryScanReasonCodes.FileChangedDuringScan, result.ReasonCode);
        DirectoryEntrySnapshot replacement = Assert.IsType<DirectoryEntrySnapshot>(result.Before);
        Assert.Equal(discovered.ByteLength, replacement.ByteLength);
        Assert.Equal(discovered.LastWriteTimeUtc, replacement.LastWriteTimeUtc);
        Assert.NotEqual(discovered.Identity, replacement.Identity);
    }

    [Fact]
    public void RootReplacementIsRejectedBeforeEnumeration()
    {
        using var fixture = new PhysicalIdentityFixture();
        var fileSystem = new PhysicalDirectoryScanFileSystem();
        DirectoryEntrySnapshot expected = Inspect(fileSystem, fixture.RootPath);
        string displacedRoot = Path.Combine(fixture.ContainerPath, "displaced-root");
        Directory.Move(fixture.RootPath, displacedRoot);
        Directory.CreateDirectory(fixture.RootPath);

        DirectoryEnumerationResult enumeration = fileSystem.EnumerateDirectory(fixture.RootPath, expected);

        Assert.Throws<DirectoryNotFoundException>(() => enumeration.Entries.ToArray());
    }

    [Fact]
    public void ChildDirectoryReplacementIsRejectedBeforeTraversal()
    {
        using var fixture = new PhysicalIdentityFixture();
        string childPath = Path.Combine(fixture.RootPath, "child");
        Directory.CreateDirectory(childPath);
        var fileSystem = new PhysicalDirectoryScanFileSystem();
        DirectoryEntrySnapshot root = Inspect(fileSystem, fixture.RootPath);
        DirectoryEntrySnapshot expectedChild = Assert.Single(
            fileSystem.EnumerateDirectory(fixture.RootPath, root).Entries);
        string displacedChild = Path.Combine(fixture.RootPath, "displaced-child");
        Directory.Move(childPath, displacedChild);
        Directory.CreateDirectory(childPath);

        DirectoryEnumerationResult enumeration = fileSystem.EnumerateDirectory(childPath, expectedChild);

        Assert.Throws<DirectoryNotFoundException>(() => enumeration.Entries.ToArray());
    }

    private static DirectoryEntrySnapshot Inspect(PhysicalDirectoryScanFileSystem fileSystem,
                                                   string fullPath)
    {
        DirectoryPathResult inspection = fileSystem.InspectPath(fullPath);
        Assert.True(inspection.Succeeded);
        DirectoryEntrySnapshot snapshot = Assert.IsType<DirectoryEntrySnapshot>(inspection.Snapshot);
        Assert.True(snapshot.Identity.HasValue);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.ResolvedPath));
        return snapshot;
    }

    private sealed class PhysicalIdentityFixture : IDisposable
    {
        internal PhysicalIdentityFixture()
        {
            ContainerPath = Path.Combine(Path.GetTempPath(), $"saddlerag-identity-{Guid.NewGuid():N}");
            RootPath = Path.Combine(ContainerPath, "root");
            Directory.CreateDirectory(RootPath);
        }

        internal string ContainerPath { get; }

        internal string RootPath { get; }

        public void Dispose()
        {
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
            string canonicalContainer = Path.GetFullPath(ContainerPath);
            bool owned = canonicalContainer.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                         && Path.GetFileName(canonicalContainer).StartsWith("saddlerag-identity-",
                                                                           StringComparison.Ordinal);
            if (owned && Directory.Exists(canonicalContainer))
            {
                FileAttributes attributes = File.GetAttributes(canonicalContainer);
                if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                    Directory.Delete(canonicalContainer, recursive: true);
            }
        }
    }
}
