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
                                         endpoint);
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
    public const string OfficialInstallUrl = "https://github.com/docling-project/docling-serve#installation";
    public const string OfficialReleaseUrl = "https://github.com/docling-project/docling-serve/releases";
    public const string OfficialApiUrl = "https://docling-project.github.io/docling/usage/api_server/";

    private const string InstructionTemplate = """
                                               Docling Serve is an optional prerequisite that you install and operate yourself.
                                               SaddleRAG was compatibility-tested with Docling Serve {0}; this is an example, not a license decision or automatic installation.

                                               In the PowerShell session or startup task that you own for the Docling process, set these process-scoped values:
                                               PYTHONUTF8=1
                                               TORCH_COMPILE_DISABLE=1

                                               A compatibility-tested Python example is:
                                               py -3.12 -m pip install "docling-serve[ui]=={0}"
                                               $env:PYTHONUTF8 = "1"
                                               $env:TORCH_COMPILE_DISABLE = "1"
                                               docling-serve run

                                               Then configure or test the endpoint {1}. SaddleRAG checks /health, /ready, and a known conversion; it never runs these commands or controls Docling.
                                               """;
    private const string OwnershipNotice =
        "The user decides whether to install and use Docling and owns its licenses, models, process, and startup configuration.";
}
