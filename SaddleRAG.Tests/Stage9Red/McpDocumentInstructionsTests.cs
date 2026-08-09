// Stage 9 acceptance coverage.

using System.Reflection;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Mcp;

public sealed class McpDocumentInstructionsTests
{
    [Fact]
    public void ServerAndFullPromptExplainExplicitManualDirectoryWorkflowAndDoclingRecovery()
    {
        string root = ResolveRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(root, "SaddleRAG.Mcp", "Program.cs"));
        string prompt = File.ReadAllText(Path.Combine(root,
                                                      "SaddleRAG.ClientIntegration",
                                                      "Resources",
                                                      "saddlerag-first.md"));

        foreach (string text in new[] { program, prompt })
        {
            Assert.Contains("register_directory_library", text, StringComparison.Ordinal);
            Assert.Contains("scan_directory_library", text, StringComparison.Ordinal);
            Assert.Contains("explicit", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("manual", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("get_document_ingestion_status", text, StringComparison.Ordinal);
            Assert.Contains("get_docling_install_instructions", text, StringComparison.Ordinal);
            Assert.Contains("user-managed", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("does not install", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ScanToolCannotRegisterOrReplaceRootPath()
    {
        MethodInfo scan = typeof(DirectoryLibraryTools).GetMethod(nameof(DirectoryLibraryTools.ScanDirectoryLibrary))!;
        string[] parameterNames = scan.GetParameters().Select(p => p.Name!).ToArray();

        Assert.Contains("library", parameterNames);
        Assert.DoesNotContain("path", parameterNames);
        Assert.DoesNotContain("root", parameterNames);
        Assert.DoesNotContain("directory", parameterNames);
    }

    [Fact]
    public void RegisterToolRequiresExplicitPathAndScanToolHasNoSchedulingInputs()
    {
        MethodInfo register = typeof(DirectoryLibraryTools)
                              .GetMethod(nameof(DirectoryLibraryTools.RegisterDirectoryLibrary))!;
        MethodInfo scan = typeof(DirectoryLibraryTools)
                          .GetMethod(nameof(DirectoryLibraryTools.ScanDirectoryLibrary))!;

        ParameterInfo path = register.GetParameters().Single(p => p.Name == "path");
        Assert.False(path.HasDefaultValue);

        string[] prohibited = ["schedule", "interval", "watch", "cron", "startup", "automatic"];
        foreach (ParameterInfo parameter in scan.GetParameters())
            Assert.DoesNotContain(prohibited,
                                  value => string.Equals(value, parameter.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InstructionsNeverClaimFolderChangesOrStartupTriggerScans()
    {
        string root = ResolveRepositoryRoot();
        string combined = string.Join('\n',
                                      File.ReadAllText(Path.Combine(root, "SaddleRAG.Mcp", "Program.cs")),
                                      File.ReadAllText(Path.Combine(root,
                                                                    "SaddleRAG.ClientIntegration",
                                                                    "Resources",
                                                                    "saddlerag-first.md")));

        Assert.DoesNotContain("automatically scan", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("scan on startup", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("watch the folder", combined, StringComparison.OrdinalIgnoreCase);
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
