// Stage 9 acceptance coverage.

using System.Xml.Linq;

namespace SaddleRAG.Tests.Installer;

public sealed class PackageWxsDoclingDialogTests
{
    [Fact]
    public void DoclingDialogIsOptionalAndPlacedImmediatelyAfterOllama()
    {
        XDocument package = LoadPackage();
        XNamespace ns = WixNamespace;
        XElement ollama = Dialog(package, ns, "OllamaDlg");
        XElement docling = Dialog(package, ns, "DoclingDlg");

        Assert.Equal("DoclingDlg", NavigationTarget(ollama, ns, "Next"));
        Assert.Equal("OllamaDlg", NavigationTarget(docling, ns, "Back"));
        Assert.Equal("ExternalToolsDlg", NavigationTarget(docling, ns, "Next"));

        XElement next = Control(docling, ns, "Next");
        Assert.DoesNotContain(next.Descendants(ns + "Publish"),
                              p => p.Attribute("Condition") != null);
    }

    [Fact]
    public void DialogShowsEndpointOwnershipOfficialLinksHealthUrlAndDetailedStatus()
    {
        XDocument package = LoadPackage();
        XNamespace ns = WixNamespace;
        XElement dialog = Dialog(package, ns, "DoclingDlg");
        string text = string.Join('\n', dialog.Descendants(ns + "Control")
                                               .Select(c => (string?)c.Attribute("Text") ?? string.Empty));

        XElement endpoint = package.Descendants(ns + "Property")
                                   .Single(e => (string?)e.Attribute("Id") == "DOCLINGENDPOINT");
        Assert.Equal("http://localhost:5001", (string?)endpoint.Attribute("Value"));
        Assert.Contains("optional", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user-managed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("separately installed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("license", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never installs, licenses, configures, or upgrades", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unauthenticated", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only when you configure OcrEngine", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[DOCLINGHEALTHURL]", text, StringComparison.Ordinal);
        Assert.Contains("[DOCLINGSTATUS]", text, StringComparison.Ordinal);

        XElement status = package.Descendants(ns + "Property")
                                 .Single(e => (string?)e.Attribute("Id") == "DOCLINGSTATUS");
        string? defaultStatus = (string?)status.Attribute("Value");
        Assert.NotNull(defaultStatus);
        Assert.Contains("unauthenticated", defaultStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("protected runtime settings", defaultStatus, StringComparison.OrdinalIgnoreCase);

        foreach (string controlId in new[]
                 {
                     "OpenInstallInstructions", "OpenReleases", "OpenApiDocumentation",
                     "OpenTesseractInstructions", "TestDocling"
                 })
            _ = Control(dialog, ns, controlId);
    }

    [Fact]
    public void ExternalToolsDialogCapturesPathsOnlyAndTreatsBlankAsAutoDetect()
    {
        XDocument package = LoadPackage();
        XNamespace ns = WixNamespace;
        XElement dialog = Dialog(package, ns, "ExternalToolsDlg");
        string text = string.Join('\n',
                                  dialog.Descendants(ns + "Control")
                                        .Select(c => (string?)c.Attribute("Text") ?? string.Empty));
        string[] boundProperties = dialog.Descendants(ns + "Control")
                                         .Select(c => (string?)c.Attribute("Property") ?? string.Empty)
                                         .ToArray();

        Assert.Equal("DoclingDlg", NavigationTarget(dialog, ns, "Back"));
        Assert.Equal("ExecutionModeDlg", NavigationTarget(dialog, ns, "Next"));
        Assert.Contains("DOCLINGCOMMAND", boundProperties);
        Assert.Contains("DOCLINGARGS", boundProperties);
        Assert.Contains("TESSERACTDIR", boundProperties);
        Assert.Contains("TESSDATADIR", boundProperties);
        Assert.Contains("auto-detect", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never installs, licenses, configures, or upgrades", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("optional", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalToolRegistrationRunsAsTheInstallingUserAndOnlyRecordsPaths()
    {
        XDocument package = LoadPackage();
        XNamespace ns = WixNamespace;
        XElement action = package.Descendants(ns + "CustomAction")
                                 .Single(e => (string?)e.Attribute("Id") == "RegisterExternalTools");

        // The MSI's own config step runs as SYSTEM, where %LOCALAPPDATA% is the service
        // profile. This one must impersonate or the registry never reaches the user.
        Assert.Equal("yes", (string?)action.Attribute("Impersonate"));
        Assert.Equal("deferred", (string?)action.Attribute("Execute"));
        Assert.Equal("ignore", (string?)action.Attribute("Return"));

        XElement command = package.Descendants(ns + "SetProperty")
                                  .Single(e => (string?)e.Attribute("Id") == "RegisterExternalTools");
        string value = (string?)command.Attribute("Value") ?? string.Empty;
        Assert.Contains("register-external-tools", value, StringComparison.Ordinal);
        Assert.Contains("[DOCLINGCOMMAND]", value, StringComparison.Ordinal);
        Assert.Contains("[TESSERACTDIR]", value, StringComparison.Ordinal);
    }

    [Fact]
    public void TestButtonRunsBoundedHealthReadinessAndAsyncOwnedConversionProbe()
    {
        XDocument package = LoadPackage();
        XNamespace ns = WixNamespace;
        XElement dialog = Dialog(package, ns, "DoclingDlg");
        XElement test = Control(dialog, ns, "TestDocling");
        Assert.Contains(test.Descendants(ns + "Publish"),
                        p => (string?)p.Attribute("Event") == "DoAction"
                             && (string?)p.Attribute("Value") == "TestDoclingConnection");

        string source = File.ReadAllText(Path.Combine(InstallerDirectory(), "TestDoclingConnection.js"));
        Assert.Contains("/health", source, StringComparison.Ordinal);
        Assert.Contains("/ready", source, StringComparison.Ordinal);
        Assert.Contains("/v1/convert/file/async", source, StringComparison.Ordinal);
        Assert.Contains("/v1/status/poll/", source, StringComparison.Ordinal);
        Assert.Contains("?wait=5", source, StringComparison.Ordinal);
        Assert.Contains("/v1/result/", source, StringComparison.Ordinal);
        Assert.Contains("600000", source, StringComparison.Ordinal);
        Assert.Contains("_waitWithinDeadline(_conversionDeadline, _conversionPollMilliseconds)",
                        source,
                        StringComparison.Ordinal);
        Assert.Contains("_poll.status === 404", source, StringComparison.Ordinal);
        Assert.Contains("[2000, 4000, 8000]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_endpoint + \"/v1/convert/file\";", source, StringComparison.Ordinal);
        Assert.Contains("_authority.indexOf(\"@\") >= 0", source, StringComparison.Ordinal);
        Assert.Contains("/\\s/.test(value)", source, StringComparison.Ordinal);
        Assert.Contains("_validator.open(\"GET\", value, false)", source, StringComparison.Ordinal);
        Assert.Contains("without embedded credentials, query, or fragment", source, StringComparison.Ordinal);
        Assert.Contains("SaddleRAG-owned", source, StringComparison.OrdinalIgnoreCase);

        foreach (string reason in new[]
                 {
                     "DOCLING_ENDPOINT_UNREACHABLE", "DOCLING_HEALTH_TIMEOUT", "DOCLING_UNAUTHORIZED",
                     "DOCLING_HEALTH_INVALID", "DOCLING_API_INCOMPATIBLE", "DOCLING_MODELS_UNAVAILABLE",
                     "DOCLING_CONVERSION_TIMEOUT", "DOCLING_CONVERSION_FAILED", "DOCLING_READY"
                 })
            Assert.Contains(reason, source, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialLinkActionsUseTheVerifiedDoclingLocations()
    {
        string combined = string.Join('\n',
                                      new[]
                                      {
                                          "OpenDoclingInstallInstructions.js",
                                          "OpenDoclingReleases.js",
                                          "OpenDoclingApiDocumentation.js",
                                          "OpenTesseractInstallInstructions.js"
                                      }.Select(file => File.ReadAllText(Path.Combine(InstallerDirectory(), file))));

        Assert.Contains("https://docling-project.github.io/docling/usage/api_server/deployment/",
                        combined,
                        StringComparison.Ordinal);
        Assert.Contains("https://github.com/docling-project/docling-serve/releases/latest",
                        combined,
                        StringComparison.Ordinal);
        Assert.Contains("https://docling-project.github.io/docling/usage/api_server/",
                        combined,
                        StringComparison.Ordinal);
        Assert.Contains("https://tesseract-ocr.github.io/tessdoc/Installation.html",
                        combined,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceExecutableIsExcludedFromHarvestAndOwnsServiceComponentKeyPath()
    {
        XDocument package = LoadPackage();
        XNamespace ns = WixNamespace;
        XElement files = package.Descendants(ns + "ComponentGroup")
                                .Single(e => (string?)e.Attribute("Id") == "PublishOutput")
                                .Element(ns + "Files")!;

        Assert.Null(files.Attribute("Exclude"));
        _ = Assert.Single(files.Elements(ns + "Exclude"),
                          exclude => (string?)exclude.Attribute("Files")
                                     == @"$(var.PublishDir)\SaddleRAG.Mcp.exe");

        XElement serviceComponent = package.Descendants(ns + "Component")
                                           .Single(e => (string?)e.Attribute("Id") == "ServiceComponent");
        XElement executable = Assert.Single(serviceComponent.Elements(ns + "File"));
        Assert.Equal(@"$(var.PublishDir)\SaddleRAG.Mcp.exe", (string?)executable.Attribute("Source"));
        Assert.Equal("yes", (string?)executable.Attribute("KeyPath"));
    }

    private static XElement Dialog(XDocument document, XNamespace ns, string id) =>
        document.Descendants(ns + "Dialog").Single(e => (string?)e.Attribute("Id") == id);

    private static XElement Control(XElement dialog, XNamespace ns, string id) =>
        dialog.Descendants(ns + "Control").Single(e => (string?)e.Attribute("Id") == id);

    private static string? NavigationTarget(XElement dialog, XNamespace ns, string controlId) =>
        Control(dialog, ns, controlId)
            .Descendants(ns + "Publish")
            .Single(p => (string?)p.Attribute("Event") == "NewDialog")
            .Attribute("Value")?.Value;

    private static XDocument LoadPackage() =>
        XDocument.Load(Path.Combine(InstallerDirectory(), "Package.wxs"));

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

    private const string WixNamespace = "http://wixtoolset.org/schemas/v4/wxs";
}
