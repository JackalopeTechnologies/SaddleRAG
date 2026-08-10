// DoclingInstallInstructions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Ingestion.Documents.Docling;

/// <summary>
///     Documentation-only guidance for the user-owned Docling prerequisite.
/// </summary>
public static class DoclingInstallInstructions
{
    public static DoclingInstallationGuide Create(DoclingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var validation = settings.Validate();
        var endpoint = DoclingEndpointDisplay.Get(validation);
        if (string.IsNullOrEmpty(endpoint))
            endpoint = DoclingSettings.DefaultEndpoint;

        var instructions = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                         InstructionTemplate,
                                         CompatibilityTestedVersion,
                                         endpoint,
                                         OfficialTesseractInstallUrl);
        string healthTestUrl = $"{endpoint.TrimEnd('/')}/health";
        return new DoclingInstallationGuide(CompatibilityTestedVersion,
                                            OfficialInstallUrl,
                                            OfficialReleaseUrl,
                                            OfficialApiUrl,
                                            endpoint,
                                            healthTestUrl,
                                            instructions,
                                            OwnershipNotice);
    }

    public const string CompatibilityTestedVersion = "1.29.0";
    public const string OfficialInstallUrl = "https://docling-project.github.io/docling/usage/api_server/deployment/";
    public const string OfficialReleaseUrl = "https://github.com/docling-project/docling-serve/releases/latest";
    public const string OfficialApiUrl = "https://docling-project.github.io/docling/usage/api_server/";
    public const string OfficialTesseractInstallUrl = "https://tesseract-ocr.github.io/tessdoc/Installation.html";

    private const string InstructionTemplate = """
                                               Docling Serve is an optional prerequisite that you install and operate yourself.
                                               SaddleRAG was compatibility-tested with Docling Serve {0}; this is an example, not a license decision or automatic installation.

                                               In the PowerShell session or startup task that you own for the Docling process, set these process-scoped values:
                                               PYTHONUTF8=1
                                               TORCH_COMPILE_DISABLE=1

                                               A compatibility-tested Python example is:
                                               py -3.12 -m venv .venv
                                               .\.venv\Scripts\python -m pip install "docling-serve[ui]=={0}"
                                               $env:PYTHONUTF8 = "1"
                                               $env:TORCH_COMPILE_DISABLE = "1"
                                               .\.venv\Scripts\docling-serve run

                                               Tesseract is optional. SaddleRAG asks Docling to perform OCR and sends an OCR-engine selection only when you set DocumentIngestion:Docling:OcrEngine; while that setting is empty the request omits the field entirely and Docling uses its own configured/default OCR behavior, so installing Tesseract alone does not change how documents are converted. If you deliberately configure Docling to use Tesseract, follow the official installation guide at {2}, install the required language data, and set TESSDATA_PREFIX to the tessdata directory with a trailing path separator (for example, C:\Program Files\Tesseract-OCR\tessdata\). Restart the user-owned Docling process after changing its OCR environment.

                                               Then configure or test the endpoint {1}. SaddleRAG checks /health, /ready, and a known conversion; it never runs the installation commands above. If you register a Docling start command in the SaddleRAG tray, the tray can run that command as you, at your request.

                                               The Windows installer's Test Docling action is deliberately unauthenticated and never asks for, collects, or stores secrets. For an API-key-protected endpoint, keep DocumentIngestion:Docling:ApiKey in an access-restricted runtime configuration source and verify it with SaddleRAG's runtime status/conversion probe after installation.
                                               """;
    private const string OwnershipNotice =
        "The user decides whether to install and use Docling or Tesseract and owns their licenses, models, processes, and startup configuration. SaddleRAG never installs, licenses, configures, or upgrades Docling or Tesseract. It can start a command you registered, at your request. The SaddleRAG MCP service never starts, stops, or restarts anything.";
}
