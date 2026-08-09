// ExternalToolDetectorTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class ExternalToolDetectorTests
{
    private sealed class FakeProbe : IFileSystemProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PathEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.Contains(path);
        public bool DirectoryExists(string path) => Directories.Contains(path);
        public string? FindOnPath(string fileName) => PathEntries.GetValueOrDefault(fileName);
    }

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static string StartScriptPath =>
        Path.Combine(UserProfile, "Applications", "Docling", "start-docling.ps1");

    private static string VirtualEnvExecutablePath =>
        Path.Combine(UserProfile, "Applications", "Docling", "venv", "Scripts", "docling-serve.exe");

    [Fact]
    public void PrefersTheStartScriptOverTheVirtualEnvExecutable()
    {
        FakeProbe probe = new();
        probe.Files.Add(StartScriptPath);
        probe.Files.Add(VirtualEnvExecutablePath);
        ExternalToolDetector detector = new(probe);

        DoclingRegistration? detected = detector.DetectDocling();

        Assert.NotNull(detected);
        Assert.Equal("pwsh", detected.Command);
        Assert.Contains($"\"{StartScriptPath}\"", detected.Arguments, StringComparison.Ordinal);
        Assert.Contains("-NoProfile", detected.Arguments, StringComparison.Ordinal);
        Assert.Equal(Path.GetDirectoryName(StartScriptPath), detected.WorkingDirectory);
    }

    [Fact]
    public void FallsBackToTheVirtualEnvExecutableWhenTheStartScriptIsAbsent()
    {
        FakeProbe probe = new();
        probe.Files.Add(VirtualEnvExecutablePath);
        ExternalToolDetector detector = new(probe);

        DoclingRegistration? detected = detector.DetectDocling();

        Assert.NotNull(detected);
        Assert.Equal(VirtualEnvExecutablePath, detected.Command);
        Assert.Contains("--port 5001", detected.Arguments, StringComparison.Ordinal);
        Assert.Equal(Path.GetDirectoryName(VirtualEnvExecutablePath), detected.WorkingDirectory);
    }

    [Fact]
    public void FallsBackToDoclingServeOnThePath()
    {
        FakeProbe probe = new();
        probe.PathEntries["docling-serve.exe"] = @"C:\tools\docling-serve.exe";
        ExternalToolDetector detector = new(probe);

        DoclingRegistration? detected = detector.DetectDocling();

        Assert.NotNull(detected);
        Assert.Equal(@"C:\tools\docling-serve.exe", detected.Command);
    }

    [Fact]
    public void ReturnsNoDoclingRegistrationWhenNothingIsFound()
    {
        ExternalToolDetector detector = new(new FakeProbe());

        Assert.Null(detector.DetectDocling());
    }

    [Fact]
    public void DetectsTesseractUnderProgramFilesWithItsTessdataDirectory()
    {
        string installDirectory = Path.Combine(ProgramFiles, "Tesseract-OCR");
        FakeProbe probe = new();
        probe.Files.Add(Path.Combine(installDirectory, "tesseract.exe"));
        probe.Directories.Add(Path.Combine(installDirectory, "tessdata"));
        ExternalToolDetector detector = new(probe);

        TesseractRegistration? detected = detector.DetectTesseract();

        Assert.NotNull(detected);
        Assert.Equal(installDirectory, detected.ExecutableDirectory);
        Assert.Equal(Path.Combine(installDirectory, "tessdata"), detected.TessdataDirectory);
    }

    [Fact]
    public void FallsBackToTheThirtyTwoBitProgramFilesLocation()
    {
        string installDirectory = Path.Combine(ProgramFilesX86, "Tesseract-OCR");
        FakeProbe probe = new();
        probe.Files.Add(Path.Combine(installDirectory, "tesseract.exe"));
        ExternalToolDetector detector = new(probe);

        TesseractRegistration? detected = detector.DetectTesseract();

        Assert.NotNull(detected);
        Assert.Equal(installDirectory, detected.ExecutableDirectory);
    }

    [Fact]
    public void FallsBackToTesseractOnThePath()
    {
        FakeProbe probe = new();
        probe.PathEntries["tesseract.exe"] = @"C:\tools\tess\tesseract.exe";
        ExternalToolDetector detector = new(probe);

        TesseractRegistration? detected = detector.DetectTesseract();

        Assert.NotNull(detected);
        Assert.Equal(@"C:\tools\tess", detected.ExecutableDirectory);
    }

    [Fact]
    public void TessdataDirectoryIsEmptyWhenTheFolderIsAbsent()
    {
        string installDirectory = Path.Combine(ProgramFiles, "Tesseract-OCR");
        FakeProbe probe = new();
        probe.Files.Add(Path.Combine(installDirectory, "tesseract.exe"));
        ExternalToolDetector detector = new(probe);

        TesseractRegistration? detected = detector.DetectTesseract();

        Assert.NotNull(detected);
        Assert.Equal(string.Empty, detected.TessdataDirectory);
    }

    [Fact]
    public void ReturnsNoTesseractRegistrationWhenNothingIsFound()
    {
        ExternalToolDetector detector = new(new FakeProbe());

        Assert.Null(detector.DetectTesseract());
    }
}
