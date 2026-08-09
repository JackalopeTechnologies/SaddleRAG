// Stage 9 acceptance coverage.

using System.Xml.Linq;
using SaddleRAG.Ingestion.Documents.Docling;

namespace SaddleRAG.Tests.Installer;

public sealed class DoclingInstallerRulesTests
{
    [Fact]
    public void InstallerContainsNoDoclingDistributionModelOrManagedServicePayload()
    {
        string installer = InstallerDirectory();
        string[] forbiddenExtensions = [".whl", ".pt", ".pth", ".safetensors", ".onnx", ".gguf"];
        string[] files = Directory.GetFiles(installer, "*", SearchOption.AllDirectories);

        Assert.DoesNotContain(files,
                              path => forbiddenExtensions.Contains(Path.GetExtension(path),
                                                                    StringComparer.OrdinalIgnoreCase));

        XDocument package = XDocument.Load(Path.Combine(installer, "Package.wxs"));
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        IEnumerable<string> packagedSources = package.Descendants()
                                                     .Where(e => e.Name == ns + "File"
                                                                 || e.Name == ns + "Payload"
                                                                 || e.Name == ns + "ExePackage"
                                                                 || e.Name == ns + "MsiPackage")
                                                     .SelectMany(e => e.Attributes())
                                                     .Select(a => a.Value);
        Assert.DoesNotContain(packagedSources,
                              value => value.Contains("docling", StringComparison.OrdinalIgnoreCase)
                                       || value.Contains("huggingface", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DoclingActionsAreLimitedToOfficialLinksConfigurationAndReadOnlyProbe()
    {
        XDocument package = XDocument.Load(Path.Combine(InstallerDirectory(), "Package.wxs"));
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        string[] actionIds = package.Descendants()
                                    .Where(e => e.Name == ns + "CustomAction" || e.Name == ns + "SetProperty")
                                    .Select(e => (string?)e.Attribute("Id"))
                                    .Where(id => id != null
                                                 && (id.Contains("Docling", StringComparison.OrdinalIgnoreCase)
                                                     || id.Contains("Tesseract", StringComparison.OrdinalIgnoreCase)))
                                    .Cast<string>()
                                    .Distinct(StringComparer.Ordinal)
                                    .ToArray();

        Assert.NotEmpty(actionIds);
        Assert.All(actionIds,
                   id => Assert.Contains(id,
                                         new[]
                                         {
                                             "TestDoclingConnection",
                                             "OpenDoclingInstallInstructions",
                                             "OpenDoclingReleases",
                                             "OpenDoclingApiDocumentation",
                                             "OpenTesseractInstallInstructions",
                                             "DOCLING_E"
                                         }));

        IEnumerable<string> actionValues = package.Descendants(ns + "SetProperty")
                                                  .Where(e => ((string?)e.Attribute("Id"))
                                                             ?.Contains("Docling",
                                                                        StringComparison.OrdinalIgnoreCase) == true)
                                                  .Select(e => (string?)e.Attribute("Value") ?? string.Empty);
        foreach (string value in actionValues)
        {
            Assert.DoesNotContain("pip install", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("winget", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("service", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("schtasks", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("start-process", value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void InstallerSourcesNeverUseTaskSchedulerOrDoclingLifecycleControl()
    {
        string combined = string.Join('\n',
                                      Directory.GetFiles(InstallerDirectory(), "*", SearchOption.TopDirectoryOnly)
                                               .Where(IsInstallerSource)
                                               .Select(File.ReadAllText));

        foreach (string forbidden in new[]
                 {
                     "schtasks.exe", "schtasks ", "Schedule.Service", "Register-ScheduledTask",
                     "New-ScheduledTask", "Start-ScheduledTask", "Stop-ScheduledTask"
                 })
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);

        string probePath = Path.Combine(InstallerDirectory(), "TestDoclingConnection.js");
        string probe = File.ReadAllText(probePath);
        foreach (string forbidden in new[]
                 {
                     "pip install", "winget", "Invoke-WebRequest", "DownloadFile", "New-Service",
                     "Start-Service", "Stop-Service", "sc.exe", "ServiceController", "Process.Start",
                     "WScript.Shell"
                 })
            Assert.DoesNotContain(forbidden, probe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedDocumentationStatesUserOwnershipAndOnlyLinksOfficialSources()
    {
        DoclingInstallationGuide guide = DoclingInstallInstructions.Create(new DoclingSettings());

        Assert.Equal("1.29.0", guide.CompatibilityTestedVersion);
        Assert.StartsWith("https://docling-project.github.io/", guide.OfficialInstallUrl);
        Assert.StartsWith("https://github.com/docling-project/", guide.OfficialReleaseUrl);
        Assert.StartsWith("https://docling-project.github.io/", guide.OfficialApiUrl);
        Assert.Contains(DoclingInstallInstructions.OfficialTesseractInstallUrl,
                        guide.Instructions,
                        StringComparison.Ordinal);
        Assert.Contains("user", guide.OwnershipNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("startup", guide.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PYTHONUTF8=1", guide.Instructions, StringComparison.Ordinal);
        Assert.Contains("TORCH_COMPILE_DISABLE=1", guide.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerContainsNoDirectoryScanTrigger()
    {
        string combined = string.Join('\n',
                                      Directory.GetFiles(InstallerDirectory(), "*", SearchOption.TopDirectoryOnly)
                                               .Where(IsInstallerSource)
                                               .Select(File.ReadAllText));

        Assert.DoesNotContain("scan_directory_library", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DirectoryScanJobRunner", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/monitor/directory-libraries/", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("QueueAsync", combined, StringComparison.Ordinal);
    }

    private static bool IsInstallerSource(string path) =>
        Path.GetExtension(path).Equals(".wxs", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".js", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase);

    private static string InstallerDirectory() =>
        Path.Combine(ResolveRepositoryRoot(), "SaddleRAG.Installer");

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "SaddleRAG.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
        return current.FullName;
    }
}
