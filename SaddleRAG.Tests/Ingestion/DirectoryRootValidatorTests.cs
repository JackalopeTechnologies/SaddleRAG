// DirectoryRootValidatorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.Ingestion.Scanning;

namespace SaddleRAG.Tests.Ingestion;

public sealed class DirectoryRootValidatorTests
{
    [Fact]
    public void EmptyAndRelativeRootsHaveDistinctReasons()
    {
        var validator = new DirectoryRootValidator(new ScriptedDirectoryScanFileSystem());

        var empty = validator.Validate(string.Empty);
        var relative = validator.Validate("manuals");

        Assert.False(empty.Succeeded);
        Assert.Equal(DirectoryScanReasonCodes.RootPathRequired, empty.ReasonCode);
        Assert.False(relative.Succeeded);
        Assert.Equal(DirectoryScanReasonCodes.RootPathNotAbsolute, relative.ReasonCode);
    }

    [Fact]
    public void MissingAndAccessDeniedRootsHaveDistinctReasonsWithoutRawDetails()
    {
        var fileSystem = new ScriptedDirectoryScanFileSystem();
        fileSystem.SetInspection(MissingRoot,
                                 new DirectoryPathResult(null,
                                                         DirectoryScanReasonCodes.RootNotFound,
                                                         new DirectoryNotFoundException(MissingRoot)));
        fileSystem.SetInspection(DeniedRoot,
                                 new DirectoryPathResult(null,
                                                         DirectoryScanReasonCodes.RootAccessDenied,
                                                         new UnauthorizedAccessException(DeniedRoot)));
        var validator = new DirectoryRootValidator(fileSystem);

        var missing = validator.Validate(MissingRoot);
        var denied = validator.Validate(DeniedRoot);

        Assert.Equal(DirectoryScanReasonCodes.RootNotFound, missing.ReasonCode);
        Assert.DoesNotContain(MissingRoot, missing.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DirectoryScanReasonCodes.RootAccessDenied, denied.ReasonCode);
        Assert.DoesNotContain(DeniedRoot, denied.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileAndReparseRootsAreRejectedDistinctly()
    {
        var fileSystem = new ScriptedDirectoryScanFileSystem();
        fileSystem.SetInspection(FileRoot,
                                 SuccessSnapshot(FileRoot, FileAttributes.Normal));
        fileSystem.SetInspection(ReparseRoot,
                                 SuccessSnapshot(ReparseRoot,
                                                 FileAttributes.Directory | FileAttributes.ReparsePoint));
        var validator = new DirectoryRootValidator(fileSystem);

        var file = validator.Validate(FileRoot);
        var reparse = validator.Validate(ReparseRoot);

        Assert.Equal(DirectoryScanReasonCodes.RootNotDirectory, file.ReasonCode);
        Assert.Equal(DirectoryScanReasonCodes.RootReparsePointNotAllowed, reparse.ReasonCode);
    }

    [Fact]
    public void ValidRootIsCanonicalizedWithoutReturningItInDetail()
    {
        var fileSystem = new ScriptedDirectoryScanFileSystem();
        fileSystem.SetInspection(ValidRoot,
                                 SuccessSnapshot(ValidRoot, FileAttributes.Directory));
        var validator = new DirectoryRootValidator(fileSystem);

        var result = validator.Validate(ValidRoot);

        Assert.True(result.Succeeded);
        Assert.Equal(Path.GetFullPath(ValidRoot), result.CanonicalRoot);
        Assert.DoesNotContain(ValidRoot, result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static DirectoryPathResult SuccessSnapshot(string path, FileAttributes attributes) =>
        new(new DirectoryEntrySnapshot(path, attributes, 0, SourceTime), string.Empty, null);

    private static readonly DateTime SourceTime = new(year: 2026,
                                                      month: 8,
                                                      day: 4,
                                                      hour: 12,
                                                      minute: 0,
                                                      second: 0,
                                                      DateTimeKind.Utc);
    private const string MissingRoot = "C:\\missing-manuals";
    private const string DeniedRoot = "C:\\denied-manuals";
    private const string FileRoot = "C:\\manual.pdf";
    private const string ReparseRoot = "C:\\linked-manuals";
    private const string ValidRoot = "C:\\manuals";
}
