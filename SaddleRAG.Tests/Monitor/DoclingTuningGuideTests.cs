// DoclingTuningGuideTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Tests.Monitor;

/// <summary>
///     The tuning guide is only useful if it stays reachable and keeps saying the two
///     things that are easy to get wrong: the GPU is opt-in through a CUDA PyTorch build,
///     and choosing a CPU-only OCR engine spends that GPU on nothing.
/// </summary>
public sealed class DoclingTuningGuideTests
{
    [Fact]
    public void GuideKeepsTheUserManagedBoundaryAndTheTwoEasilyMissedFacts()
    {
        string guide = File.ReadAllText(Path.Combine(ResolveRepositoryRoot(),
                                                                  "SaddleRAG.ClientIntegration",
                                                                  "Resources",
                                                                  "docling-tuning.md"));

        // Deliberately within one line: the guide is checked out with CRLF on Windows and LF
        // on Linux, so any expected substring spanning a line break passes on one CI leg and
        // fails on the other.
        Assert.Contains("installs, licenses, configures, starts, stops, or upgrades it",
                        guide,
                        StringComparison.Ordinal);
        // The index, not just the flag: a reader who copies one CUDA index blindly can
        // silently downgrade PyTorch, so the guide has to teach querying the index first.
        Assert.Contains("pip index versions torch --index-url", guide, StringComparison.Ordinal);
        Assert.Contains("the version you already", guide, StringComparison.Ordinal);
        Assert.Contains("torch.cuda.is_available()", guide, StringComparison.Ordinal);
        Assert.Contains("Tesseract is a CPU-only", guide, StringComparison.Ordinal);
        Assert.Contains("DOCLING_DEVICE", guide, StringComparison.Ordinal);
        Assert.Contains("DOCLING_NUM_THREADS", guide, StringComparison.Ordinal);
        Assert.Contains("Stop Docling first", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void GuideDescribesTheConversionOptionsSaddleRagActuallySends()
    {
        string root = ResolveRepositoryRoot();
        string guide = File.ReadAllText(Path.Combine(root,
                                                     "SaddleRAG.ClientIntegration",
                                                     "Resources",
                                                     "docling-tuning.md"));
        string client = File.ReadAllText(Path.Combine(root,
                                                      "SaddleRAG.Ingestion",
                                                      "Documents",
                                                      "Docling",
                                                      "DoclingClient.cs"));

        // Each option the guide claims is already off has to actually be sent as false,
        // so the "nothing to win here" advice cannot rot into a lie.
        foreach(string field in new[]
                {
                    "PictureDescriptionField", "PictureClassificationField", "CodeEnrichmentField",
                    "FormulaEnrichmentField"
                })
            Assert.Contains($"AddFormField(multipart, {field}, FalseValue)", client, StringComparison.Ordinal);

        Assert.Contains("AddFormField(multipart, DoOcrField, TrueValue)", client, StringComparison.Ordinal);
        Assert.Contains("SaddleRAG already has the enrichment stages off", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorAndReadmePointAtTheGuide()
    {
        string root = ResolveRepositoryRoot();
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        string page = File.ReadAllText(Path.Combine(root,
                                                    "SaddleRAG.Monitor",
                                                    "Pages",
                                                    "DirectoryLibrariesPage.razor"));

        foreach(string surface in new[] { readme, page })
            Assert.Contains("Resources/docling-tuning.md", surface, StringComparison.Ordinal);
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
