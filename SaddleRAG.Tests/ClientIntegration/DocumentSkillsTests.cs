// DocumentSkillsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using SaddleRAG.ClientIntegration.Skills;

namespace SaddleRAG.Tests.ClientIntegration;

public sealed class DocumentSkillsTests
{
    [Fact]
    public void DocumentSkillExplainsTheExplicitManualWorkflowAndStoredDocumentBehavior()
    {
        SkillContent skill = Assert.Single(SkillCatalog.All,
                                           item => item.Name == DocumentSkillName);

        AssertContainsAll(skill.Body,
                          "register_directory_library",
                          "scan_directory_library",
                          "get_document_ingestion_status",
                          "get_job_status",
                          "search_docs",
                          "manual",
                          "version",
                          "stored",
                          ".pdf",
                          ".docx");
        Assert.DoesNotContain("scan on startup", skill.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("watch the folder", skill.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoclingSetupSkillKeepsServeAndMcpUserManagedSeparateAndVerifiable()
    {
        SkillContent skill = Assert.Single(SkillCatalog.All,
                                           item => item.Name == DoclingSetupSkillName);

        AssertContainsAll(skill.Body,
                          "1.29.0",
                          "Python 3.12",
                          "PYTHONUTF8=1",
                          "TORCH_COMPILE_DISABLE=1",
                          "get_document_ingestion_status",
                          "get_docling_install_instructions",
                          "docling-mcp",
                          "DOCLING_SERVICE_URL",
                          "DOCLING_CONVERSION_MODE",
                          "remote",
                          "not required",
                          "user-managed",
                          "https://github.com/docling-project/docling-serve",
                          "https://github.com/docling-project/docling-mcp");
    }

    private static void AssertContainsAll(string value, params string[] expected)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(expected);
        foreach (string item in expected)
            Assert.Contains(item, value, StringComparison.OrdinalIgnoreCase);
    }

    private const string DocumentSkillName = "saddlerag-documents";
    private const string DoclingSetupSkillName = "saddlerag-docling-setup";
}
