// PhysicalDirectoryScanFileSystemIdentityTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Runtime.InteropServices;
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

    [Fact]
    public void SymbolicLinkRootRetainsReparsePointClassification()
    {
        using var fixture = new PhysicalIdentityFixture();
        string linkPath = Path.Combine(fixture.ContainerPath, "linked-root");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, fixture.RootPath);
            }
            catch(Exception error) when (error is IOException
                                         or UnauthorizedAccessException
                                         or PlatformNotSupportedException)
            {
                Assert.Skip($"Directory symbolic links are unavailable: {error.GetType().Name}.");
            }

            var fileSystem = new PhysicalDirectoryScanFileSystem();
            DirectoryEntrySnapshot snapshot = Inspect(fileSystem, linkPath);

            Assert.True(snapshot.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            if (Directory.Exists(linkPath))
                Directory.Delete(linkPath, recursive: false);
        }
    }

    [Fact]
    public void SymbolicLinkAncestorRetainsReparsePointClassification()
    {
        using var fixture = new PhysicalIdentityFixture();
        string targetContainer = Path.Combine(fixture.ContainerPath, "real-container");
        Directory.CreateDirectory(Path.Combine(targetContainer, "root"));
        string linkPath = Path.Combine(fixture.ContainerPath, "linked-container");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, targetContainer);
            }
            catch(Exception error) when (error is IOException
                                         or UnauthorizedAccessException
                                         or PlatformNotSupportedException)
            {
                Assert.Skip($"Directory symbolic links are unavailable: {error.GetType().Name}.");
            }

            string requestedRoot = Path.Combine(linkPath, "root");
            var fileSystem = new PhysicalDirectoryScanFileSystem();
            DirectoryEntrySnapshot snapshot = Inspect(fileSystem, requestedRoot);

            Assert.True(snapshot.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        finally
        {
            if (Directory.Exists(linkPath))
                Directory.Delete(linkPath, recursive: false);
        }
    }

    [Fact]
    public void WindowsShortPathAliasIsNotClassifiedAsReparsePoint()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows short paths are only available on Windows.");
        using var fixture = new PhysicalIdentityFixture();
        string shortPath = GetWindowsShortPath(fixture.RootPath);
        Assert.SkipUnless(!shortPath.Equals(fixture.RootPath, StringComparison.OrdinalIgnoreCase),
                          "The test volume did not provide a distinct 8.3 path alias.");
        var fileSystem = new PhysicalDirectoryScanFileSystem();

        DirectoryEntrySnapshot snapshot = Inspect(fileSystem, shortPath);

        Assert.False(snapshot.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    private static DirectoryEntrySnapshot Inspect(PhysicalDirectoryScanFileSystem fileSystem,
                                                   string fullPath)
    {
        DirectoryPathResult inspection = fileSystem.InspectPath(fullPath);
        Assert.True(inspection.Succeeded,
                    $"{inspection.ReasonCode}: {inspection.Error}");
        DirectoryEntrySnapshot snapshot = Assert.IsType<DirectoryEntrySnapshot>(inspection.Snapshot);
        Assert.True(snapshot.Identity.HasValue);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.ResolvedPath));
        return snapshot;
    }

    private static string GetWindowsShortPath(string fullPath)
    {
        var buffer = new StringBuilder(InitialWindowsPathCapacity);
        uint length = GetShortPathName(fullPath, buffer, (uint)buffer.Capacity);
        Assert.SkipUnless(length > 0,
                          $"Windows short-path aliases are unavailable: {Marshal.GetLastWin32Error()}.");
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetShortPathName(fullPath, buffer, (uint)buffer.Capacity);
            Assert.SkipUnless(length > 0 && length < buffer.Capacity,
                              $"Windows short-path aliases are unavailable: {Marshal.GetLastWin32Error()}.");
        }

        return buffer.ToString();
    }

    [DllImport("kernel32.dll",
               EntryPoint = "GetShortPathNameW",
               CharSet = CharSet.Unicode,
               ExactSpelling = true,
               SetLastError = true)]
    private static extern uint GetShortPathName(string longPath,
                                                StringBuilder shortPath,
                                                uint shortPathLength);

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

    private const int InitialWindowsPathCapacity = 512;
}
