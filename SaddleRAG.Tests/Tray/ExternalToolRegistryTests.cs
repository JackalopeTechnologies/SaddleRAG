// ExternalToolRegistryTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class ExternalToolRegistryTests : IDisposable
{
    public ExternalToolRegistryTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), $"saddlerag-tool-registry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mTempDir);
        mRegistryPath = Path.Combine(mTempDir, "external-tools.json");
    }

    private readonly string mTempDir;
    private readonly string mRegistryPath;

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    [Fact]
    public void RoundTripsDoclingAndTesseractIncludingTheEnvironmentDictionary()
    {
        var registry = new ExternalToolRegistry(mRegistryPath);
        DoclingRegistration docling = new(@"C:\Program Files\PowerShell\7\pwsh.exe",
                                          @"-NoProfile -ExecutionPolicy Bypass -File ""C:\Docling\start-docling.ps1""",
                                          @"C:\Docling",
                                          new Dictionary<string, string>
                                          {
                                              ["PYTHONUTF8"] = "1", ["HF_HOME"] = @"C:\Docling\hf"
                                          });
        TesseractRegistration tesseract = new(@"C:\Program Files\Tesseract-OCR",
                                              @"C:\Program Files\Tesseract-OCR\tessdata");

        registry.Write(new ExternalToolRegistration(docling, tesseract));
        ExternalToolRegistration read = registry.Read();

        Assert.NotNull(read.Docling);
        Assert.Equal(docling.Command, read.Docling.Command);
        Assert.Equal(docling.Arguments, read.Docling.Arguments);
        Assert.Equal(docling.WorkingDirectory, read.Docling.WorkingDirectory);
        Assert.Equal("1", read.Docling.Environment["PYTHONUTF8"]);
        Assert.Equal(@"C:\Docling\hf", read.Docling.Environment["HF_HOME"]);
        Assert.NotNull(read.Tesseract);
        Assert.Equal(tesseract.ExecutableDirectory, read.Tesseract.ExecutableDirectory);
        Assert.Equal(tesseract.TessdataDirectory, read.Tesseract.TessdataDirectory);
    }

    [Fact]
    public void MissingFileYieldsAnEmptyRegistration()
    {
        var registry = new ExternalToolRegistry(mRegistryPath);

        ExternalToolRegistration read = registry.Read();

        Assert.Null(read.Docling);
        Assert.Null(read.Tesseract);
    }

    [Fact]
    public void MalformedJsonYieldsAnEmptyRegistrationRatherThanThrowing()
    {
        File.WriteAllText(mRegistryPath, "{ this is not json");
        var registry = new ExternalToolRegistry(mRegistryPath);

        ExternalToolRegistration read = registry.Read();

        Assert.Null(read.Docling);
        Assert.Null(read.Tesseract);
    }

    [Fact]
    public void ADoclingEntryWithNoEnvironmentReadsAsAnEmptyDictionary()
    {
        File.WriteAllText(mRegistryPath,
                          """{"docling":{"command":"pwsh","arguments":"-File x.ps1","workingDirectory":"C:\\d"}}""");
        var registry = new ExternalToolRegistry(mRegistryPath);

        ExternalToolRegistration read = registry.Read();

        Assert.NotNull(read.Docling);
        Assert.Empty(read.Docling.Environment);
    }

    [Fact]
    public void RepeatedWritesReplaceTheFileAndLeaveNoTemporaryBehind()
    {
        var registry = new ExternalToolRegistry(mRegistryPath);
        var first = new ExternalToolRegistration(
            new DoclingRegistration("first.exe", "a", @"C:\first", new Dictionary<string, string>()),
            Tesseract: null);
        var second = new ExternalToolRegistration(
            new DoclingRegistration("second.exe", "b", @"C:\second", new Dictionary<string, string>()),
            Tesseract: null);

        registry.Write(first);
        registry.Write(second);

        ExternalToolRegistration read = registry.Read();
        Assert.NotNull(read.Docling);
        Assert.Equal("second.exe", read.Docling.Command);
        Assert.Single(Directory.GetFiles(mTempDir));
    }

    [Fact]
    public void WriteCreatesTheContainingDirectoryWhenAbsent()
    {
        string nestedPath = Path.Combine(mTempDir, "nested", "external-tools.json");
        var registry = new ExternalToolRegistry(nestedPath);

        registry.Write(new ExternalToolRegistration(
                           Docling: null,
                           new TesseractRegistration(@"C:\Tess", @"C:\Tess\tessdata")));

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void PersistedJsonUsesTheDocumentedCamelCaseShape()
    {
        var registry = new ExternalToolRegistry(mRegistryPath);
        registry.Write(new ExternalToolRegistration(
                           new DoclingRegistration("pwsh", "-File x.ps1", @"C:\d",
                                                   new Dictionary<string, string> { ["PYTHONUTF8"] = "1" }),
                           new TesseractRegistration(@"C:\Tess", @"C:\Tess\tessdata")));

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(mRegistryPath));
        JsonElement docling = json.RootElement.GetProperty("docling");
        JsonElement tesseract = json.RootElement.GetProperty("tesseract");

        Assert.Equal("pwsh", docling.GetProperty("command").GetString());
        Assert.Equal("-File x.ps1", docling.GetProperty("arguments").GetString());
        Assert.Equal(@"C:\d", docling.GetProperty("workingDirectory").GetString());
        Assert.Equal("1", docling.GetProperty("environment").GetProperty("PYTHONUTF8").GetString());
        Assert.Equal(@"C:\Tess", tesseract.GetProperty("executableDirectory").GetString());
        Assert.Equal(@"C:\Tess\tessdata", tesseract.GetProperty("tessdataDirectory").GetString());
    }

    [Fact]
    public void DefaultPathLivesUnderLocalApplicationData()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "SaddleRAG",
                                       "external-tools.json");

        Assert.Equal(expected, ExternalToolRegistry.DefaultPath);
    }
}
