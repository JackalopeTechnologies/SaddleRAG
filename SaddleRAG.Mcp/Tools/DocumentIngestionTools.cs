// DocumentIngestionTools.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SaddleRAG.Ingestion.Documents.Docling;

namespace SaddleRAG.Mcp.Tools;

/// <summary>Read-only diagnostics and documentation for optional document ingestion.</summary>
[McpServerToolType]
public static class DocumentIngestionTools
{
    [McpServerTool(Name = "get_document_ingestion_status")]
    [Description("Report the observed status of the user-managed Docling document capability. " +
                 "This read-only tool may refresh the probe but never starts, stops, installs, or changes Docling.")]
    public static async Task<string> GetDocumentIngestionStatus(
        IDoclingCapabilityService capability,
        [Description("Whether to perform a fresh bounded readiness and owned-document conversion probe.")]
        bool refresh = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        DoclingCapabilityStatus status = await capability.GetStatusAsync(refresh, ct);
        return JsonSerializer.Serialize(new
                                            {
                                                State = status.State.ToString(),
                                                status.ReasonCode,
                                                status.Detail,
                                                status.Endpoint,
                                                status.LastCheckedAt,
                                                status.Remediation
                                            },
                                        smJsonOptions);
    }

    [McpServerTool(Name = "get_docling_install_instructions")]
    [Description("Return documentation and official links for the optional user-managed Docling endpoint. " +
                 "This tool does not install, license, configure, start, stop, or upgrade anything.")]
    public static Task<string> GetDoclingInstallInstructions(DoclingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DoclingInstallationGuide guide = DoclingInstallInstructions.Create(settings);
        string result = JsonSerializer.Serialize(guide, smJsonOptions);
        return Task.FromResult(result);
    }

    private static readonly JsonSerializerOptions smJsonOptions = new() { WriteIndented = true };
}
