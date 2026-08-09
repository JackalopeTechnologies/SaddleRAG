// DirectoryLibraryFixtureTree.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text;

namespace SaddleRAG.Tests.Ingestion;

/// <summary>
///     Mutable, test-owned source directory for the Stage 6 manual-scan
///     acceptance slice. The fixture never points at user data.
/// </summary>
internal sealed class DirectoryLibraryFixtureTree : IDisposable
{
    private DirectoryLibraryFixtureTree(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public string GuidePath => Path.Combine(RootPath, GuideFileName);

    public string NotesPath => Path.Combine(RootPath, NotesFileName);

    public string PdfPath => Path.Combine(RootPath, PdfFileName);

    public string DocxPath => Path.Combine(RootPath, DocxFileName);

    public static DirectoryLibraryFixtureTree Create()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"saddlerag-directory-library-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        var result = new DirectoryLibraryFixtureTree(rootPath);
        result.Populate();
        return result;
    }

    public void AddNewDocument(string content = "New document marker")
    {
        File.WriteAllText(Path.Combine(RootPath, NewFileName), content, Encoding.UTF8);
    }

    public void ChangeGuide(string content = "# Changed guide\nChanged guide marker")
    {
        File.WriteAllText(GuidePath, content, Encoding.UTF8);
    }

    public void RemoveNotes()
    {
        File.Delete(NotesPath);
    }

    public void Dispose()
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        string canonicalRoot = Path.GetFullPath(RootPath);
        bool isOwned = canonicalRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                       && Path.GetFileName(canonicalRoot).StartsWith(OwnedDirectoryPrefix,
                                                                    StringComparison.Ordinal);
        if (isOwned && Directory.Exists(canonicalRoot))
        {
            FileAttributes attributes = File.GetAttributes(canonicalRoot);
            if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(canonicalRoot, recursive: true);
        }
    }

    private void Populate()
    {
        File.WriteAllText(GuidePath, "# Guide heading\nDirectory guide marker", Encoding.UTF8);
        File.WriteAllText(NotesPath, "Notes marker retained only in the first snapshot", Encoding.UTF8);
        File.WriteAllBytes(PdfPath, LoadOwnedFixture("saddlerag-docling-probe.pdf"));
        File.WriteAllBytes(DocxPath, LoadOwnedFixture("saddlerag-docling-probe.docx"));
        File.WriteAllText(Path.Combine(RootPath, "unsupported.bin"), "unsupported", Encoding.UTF8);
    }

    private static byte[] LoadOwnedFixture(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", "Documents", fileName);
        return File.ReadAllBytes(path);
    }

    public const string GuideFileName = "Guide.md";
    public const string NotesFileName = "Notes.txt";
    public const string PdfFileName = "Manual.pdf";
    public const string DocxFileName = "Manual.docx";
    public const string NewFileName = "New.txt";

    private const string OwnedDirectoryPrefix = "saddlerag-directory-library-";
}
