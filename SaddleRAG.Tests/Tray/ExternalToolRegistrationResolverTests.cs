// ExternalToolRegistrationResolverTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class ExternalToolRegistrationResolverTests
{
    private sealed class StubProbe : IFileSystemProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.Contains(path);
        public bool DirectoryExists(string path) => Directories.Contains(path);
        public string? FindOnPath(string fileName) => null;
    }

    private static string DetectableStartScript =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     "Applications",
                     "Docling",
                     "start-docling.ps1");

    private static ExternalToolDetector DetectorThatFinds(params string[] files)
    {
        StubProbe probe = new();
        foreach (string file in files)
            probe.Files.Add(file);

        return new ExternalToolDetector(probe);
    }

    [Fact]
    public void AnExplicitDoclingCommandWinsOverDetection()
    {
        // Built with Path.Combine rather than a literal: the suite also runs on Linux, where a
        // backslash is an ordinary filename character and Path.GetDirectoryName returns empty.
        string commandDirectory = Path.Combine(Path.GetTempPath(), "custom");
        string command = Path.Combine(commandDirectory, "docling.exe");

        ExternalToolRegistration resolved = ExternalToolRegistrationResolver.Resolve(
            ExternalToolRegistration.Empty,
            DetectorThatFinds(DetectableStartScript),
            command,
            doclingArguments: "run --port 5001",
            tesseractDirectory: null,
            tessdataDirectory: null);

        Assert.NotNull(resolved.Docling);
        Assert.Equal(command, resolved.Docling.Command);
        Assert.Equal("run --port 5001", resolved.Docling.Arguments);
        Assert.Equal(commandDirectory, resolved.Docling.WorkingDirectory);
    }

    [Fact]
    public void ABlankDoclingCommandFallsBackToDetection()
    {
        ExternalToolRegistration resolved = ExternalToolRegistrationResolver.Resolve(
            ExternalToolRegistration.Empty,
            DetectorThatFinds(DetectableStartScript),
            doclingCommand: "   ",
            doclingArguments: null,
            tesseractDirectory: null,
            tessdataDirectory: null);

        Assert.NotNull(resolved.Docling);
        Assert.Equal("pwsh", resolved.Docling.Command);
        Assert.Contains(DetectableStartScript, resolved.Docling.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExistingRegistrationIsKeptRatherThanReplacedByDetection()
    {
        ExternalToolRegistration current = new(
            new DoclingRegistration(@"C:\already\registered.exe", "--serve", @"C:\already", Environment: null),
            Tesseract: null);

        ExternalToolRegistration resolved = ExternalToolRegistrationResolver.Resolve(
            current,
            DetectorThatFinds(DetectableStartScript),
            doclingCommand: null,
            doclingArguments: null,
            tesseractDirectory: null,
            tessdataDirectory: null);

        Assert.NotNull(resolved.Docling);
        Assert.Equal(@"C:\already\registered.exe", resolved.Docling.Command);
    }

    [Fact]
    public void AnExplicitTesseractDirectoryDefaultsItsTessdataSibling()
    {
        ExternalToolRegistration resolved = ExternalToolRegistrationResolver.Resolve(
            ExternalToolRegistration.Empty,
            DetectorThatFinds(),
            doclingCommand: null,
            doclingArguments: null,
            tesseractDirectory: @"C:\Tools\Tesseract-OCR",
            tessdataDirectory: null);

        Assert.NotNull(resolved.Tesseract);
        Assert.Equal(@"C:\Tools\Tesseract-OCR", resolved.Tesseract.ExecutableDirectory);
        Assert.Equal(Path.Combine(@"C:\Tools\Tesseract-OCR", "tessdata"), resolved.Tesseract.TessdataDirectory);
    }

    [Fact]
    public void NothingProvidedAndNothingDetectedLeavesBothUnregistered()
    {
        ExternalToolRegistration resolved = ExternalToolRegistrationResolver.Resolve(
            ExternalToolRegistration.Empty,
            DetectorThatFinds(),
            doclingCommand: null,
            doclingArguments: null,
            tesseractDirectory: null,
            tessdataDirectory: null);

        Assert.Null(resolved.Docling);
        Assert.Null(resolved.Tesseract);
    }
}
