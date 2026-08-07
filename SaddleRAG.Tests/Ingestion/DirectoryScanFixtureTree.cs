// DirectoryScanFixtureTree.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text;

namespace SaddleRAG.Tests.Ingestion;

internal sealed class DirectoryScanFixtureTree : IDisposable
{
    private DirectoryScanFixtureTree(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public static DirectoryScanFixtureTree Create()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"saddlerag-directory-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        var result = new DirectoryScanFixtureTree(rootPath);
        result.Populate();
        return result;
    }

    public void Dispose()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var canonicalRoot = Path.GetFullPath(RootPath);
        var isOwned = canonicalRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                      && Path.GetFileName(canonicalRoot).StartsWith("saddlerag-directory-preview-",
                                                                   StringComparison.Ordinal);
        if (isOwned && Directory.Exists(canonicalRoot))
        {
            var attributes = File.GetAttributes(canonicalRoot);
            if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(canonicalRoot, recursive: true);
        }
    }

    private void Populate()
    {
        var nested = Path.Combine(RootPath, "Nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(RootPath, "Guide.md"), "# Guide\r\nDirectory content.", Encoding.UTF8);
        File.WriteAllText(Path.Combine(RootPath, "Notes.txt"), "duplicate content", Encoding.UTF8);
        File.WriteAllText(Path.Combine(RootPath, "Page.html"),
                          "<main><h1>Page</h1><p>Visible.</p></main>",
                          Encoding.UTF8);
        File.WriteAllText(Path.Combine(RootPath, "unsupported.bin"), "unsupported", Encoding.UTF8);
        File.WriteAllText(Path.Combine(nested, "Duplicate.txt"), "duplicate content", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(RootPath, "Manual.pdf"), LoadOwnedFixture("saddlerag-docling-probe.pdf"));
        File.WriteAllBytes(Path.Combine(RootPath, "Manual.docx"), LoadOwnedFixture("saddlerag-docling-probe.docx"));
    }

    private static byte[] LoadOwnedFixture(string fileName)
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var path = Path.Combine(projectDirectory, "TestData", "Documents", fileName);
        return File.ReadAllBytes(path);
    }
}
