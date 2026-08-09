// DocumentSetupGuidanceTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tests.Monitor;

public sealed class DocumentSetupGuidanceTests
{
    [Fact]
    public void OpeningPagesKeepDoclingAndTesseractUserManagedAndLinkOfficialGuides()
    {
        string root = ResolveRepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        string landingPage = File.ReadAllText(Path.Combine(root,
                                                            "SaddleRAG.Monitor",
                                                            "Pages",
                                                            "LandingPage.razor"));
        string clientGuide = File.ReadAllText(Path.Combine(root,
                                                            "SaddleRAG.ClientIntegration",
                                                            "Resources",
                                                            "saddlerag-docling-setup.md"));
        string instructionSource = File.ReadAllText(Path.Combine(root,
                                                                  "SaddleRAG.Ingestion",
                                                                  "Documents",
                                                                  "Docling",
                                                                  "DoclingInstallInstructions.cs"));

        Assert.Contains("SaddleRAG does **not** install", readme, StringComparison.Ordinal);
        Assert.Contains("https://docling-project.github.io/docling/usage/api_server/deployment/",
                        readme,
                        StringComparison.Ordinal);
        Assert.Contains("https://github.com/docling-project/docling-serve/releases/latest",
                        readme,
                        StringComparison.Ordinal);
        Assert.Contains("https://tesseract-ocr.github.io/tessdoc/Installation.html",
                        readme,
                        StringComparison.Ordinal);
        Assert.Contains("Test Docling", readme, StringComparison.Ordinal);
        Assert.Contains("health, model readiness", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Installing Tesseract alone does not make SaddleRAG or Docling use it",
                        readme,
                        StringComparison.Ordinal);
        Assert.Contains("set `TESSDATA_PREFIX`", readme, StringComparison.Ordinal);
        Assert.Contains("tessdata\\`", readme, StringComparison.Ordinal);
        Assert.Contains(".\\.venv\\Scripts\\docling-serve run", readme, StringComparison.Ordinal);
        Assert.Contains("deliberately unauthenticated", readme, StringComparison.Ordinal);
        Assert.Contains("never asks for, collects, or stores secrets", readme, StringComparison.Ordinal);
        Assert.Contains("DocumentIngestion:Docling:ApiKey", readme, StringComparison.Ordinal);

        Assert.Contains("user-managed Docling Serve", landingPage, StringComparison.Ordinal);
        Assert.Contains("SaddleRAG never installs or manages Docling or Tesseract",
                        landingPage,
                        StringComparison.Ordinal);
        Assert.Contains("installing Tesseract alone does not make SaddleRAG or Docling use it",
                        landingPage,
                        StringComparison.Ordinal);
        Assert.Contains("only tests unauthenticated endpoints", landingPage, StringComparison.Ordinal);
        Assert.Contains("never collects", landingPage, StringComparison.Ordinal);
        Assert.Contains("docling-project/docling-serve/releases/latest",
                        landingPage,
                        StringComparison.Ordinal);
        Assert.Contains("tesseract-ocr.github.io/tessdoc/Installation.html",
                        landingPage,
                        StringComparison.Ordinal);

        Assert.Contains("does not send Docling an OCR-engine or preset selection",
                        clientGuide,
                        StringComparison.Ordinal);
        Assert.Contains("never installs, licenses, starts, stops, restarts, upgrades, or otherwise manages Docling or Tesseract",
                        clientGuide,
                        StringComparison.Ordinal);
        Assert.Contains("set `TESSDATA_PREFIX`", clientGuide, StringComparison.Ordinal);
        Assert.Contains("tessdata\\`", clientGuide, StringComparison.Ordinal);
        Assert.Contains("py -3.12 -m venv .venv", clientGuide, StringComparison.Ordinal);
        Assert.Contains(".\\.venv\\Scripts\\docling-serve run", clientGuide, StringComparison.Ordinal);
        Assert.Contains("DocumentIngestion:Docling:ApiKey", clientGuide, StringComparison.Ordinal);
        Assert.Contains("limited to unauthenticated endpoints", clientGuide, StringComparison.Ordinal);
        Assert.Contains("never asks for, collects, or stores secrets", clientGuide, StringComparison.Ordinal);

        Assert.Contains("Installing Tesseract alone does not make SaddleRAG or Docling use it",
                        instructionSource,
                        StringComparison.Ordinal);
        Assert.Contains("set TESSDATA_PREFIX", instructionSource, StringComparison.Ordinal);
        Assert.Contains("tessdata\\)", instructionSource, StringComparison.Ordinal);
        Assert.Contains("py -3.12 -m venv .venv", instructionSource, StringComparison.Ordinal);
        Assert.Contains(".\\.venv\\Scripts\\docling-serve run", instructionSource, StringComparison.Ordinal);
        Assert.Contains("deliberately unauthenticated", instructionSource, StringComparison.Ordinal);
        Assert.Contains("never asks for, collects, or stores secrets", instructionSource, StringComparison.Ordinal);
        Assert.Contains("DocumentIngestion:Docling:ApiKey", instructionSource, StringComparison.Ordinal);
    }

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "SaddleRAG.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
        return current.FullName;
    }
}
