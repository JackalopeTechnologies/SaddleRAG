// Stage 9 acceptance coverage. Merge these cases into PatchAppSettingsTests.cs when the
// complete batch is activated. PatchAppSettingsTestDriver is the proposed
// extraction of that file's existing bounded process/temp-file helper.

using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace SaddleRAG.Tests.Installer;

public sealed class PatchAppSettingsDoclingTests
{
    [Fact]
    public void BlankInstallerInputPreservesExistingNonblankDoclingEndpointOnUpgrade()
    {
        JsonObject settings = Settings("https://docling.example:8443/api");

        JsonObject patched = PatchAppSettingsTestDriver.Run(settings, doclingEndpoint: string.Empty);

        Assert.Equal("https://docling.example:8443/api",
                     patched["DocumentIngestion"]!["Docling"]!["Endpoint"]!.GetValue<string>());
    }

    [Fact]
    public void BlankInstallerInputUsesLocalDefaultOnlyWhenExistingEndpointIsBlankOrMissing()
    {
        JsonObject blank = Settings("   ");
        JsonObject missing = Settings("http://placeholder");
        missing["DocumentIngestion"]!["Docling"]!.AsObject().Remove("Endpoint");

        JsonObject patchedBlank = PatchAppSettingsTestDriver.Run(blank, doclingEndpoint: string.Empty);
        JsonObject patchedMissing = PatchAppSettingsTestDriver.Run(missing, doclingEndpoint: string.Empty);

        Assert.Equal("http://localhost:5001",
                     patchedBlank["DocumentIngestion"]!["Docling"]!["Endpoint"]!.GetValue<string>());
        Assert.Equal("http://localhost:5001",
                     patchedMissing["DocumentIngestion"]!["Docling"]!["Endpoint"]!.GetValue<string>());
    }

    [Fact]
    public void ExplicitEndpointChangesOnlyEndpointAndPreservesSecretsTimeoutsAndUnrelatedSettings()
    {
        JsonObject settings = Settings("http://localhost:5001");
        string beforeApiKey = settings["DocumentIngestion"]!["Docling"]!["ApiKey"]!.GetValue<string>();
        int beforeTimeout = settings["DocumentIngestion"]!["Docling"]!["ConversionTimeoutSeconds"]!.GetValue<int>();
        JsonNode unrelatedBefore = settings["Arbitrary"]!.DeepClone();

        JsonObject patched = PatchAppSettingsTestDriver.Run(settings,
                                                            doclingEndpoint: "http://docling-box:5001");

        JsonObject docling = patched["DocumentIngestion"]!["Docling"]!.AsObject();
        Assert.Equal("http://docling-box:5001", docling["Endpoint"]!.GetValue<string>());
        Assert.Equal(beforeApiKey, docling["ApiKey"]!.GetValue<string>());
        Assert.Equal(beforeTimeout, docling["ConversionTimeoutSeconds"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(unrelatedBefore, patched["Arbitrary"]));
    }

    [Fact]
    public void PatchRemainsAtomicAndPackagePassesDoclingParameter()
    {
        PatchAppSettingsRun run = PatchAppSettingsTestDriver.RunWithFiles(
                                      Settings("http://localhost:5001"),
                                      doclingEndpoint: "http://docling-box:5001");

        Assert.Equal(0, run.ExitCode);
        Assert.True(File.Exists(run.AppSettingsPath));
        Assert.False(File.Exists(run.AppSettingsPath + ".tmp"));
        _ = JsonNode.Parse(File.ReadAllText(run.AppSettingsPath));

        XDocument package = XDocument.Load(Path.Combine(run.RepositoryRoot,
                                                        "SaddleRAG.Installer",
                                                        "Package.wxs"));
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        XElement alias = package.Descendants(ns + "SetProperty")
                                .Single(e => (string?)e.Attribute("Id") == "DOCLING_E");
        Assert.Equal("[DOCLINGENDPOINT_ESCAPED]", (string?)alias.Attribute("Value"));

        XElement command = package.Descendants(ns + "SetProperty")
                                  .Single(e => (string?)e.Attribute("Id") == "PatchAppSettings");
        Assert.Contains("-L \"[DOCLING_E]\"",
                        (string?)command.Attribute("Value"),
                        StringComparison.Ordinal);

        string script = File.ReadAllText(Path.Combine(run.RepositoryRoot,
                                                       "SaddleRAG.Installer",
                                                       "PatchAppSettings.ps1"));
        Assert.Contains("[Alias('L')]", script, StringComparison.Ordinal);
        Assert.Contains("$DoclingEndpoint", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySixHundredSecondBudgetIsRaisedAndStallSeeded()
    {
        JsonObject settings = Settings("http://localhost:5001", conversionTimeoutSeconds: 600);

        JsonObject patched = PatchAppSettingsTestDriver.Run(settings, doclingEndpoint: "");

        JsonObject docling = patched["DocumentIngestion"]!["Docling"]!.AsObject();
        Assert.Equal(expected: 14400, docling["ConversionTimeoutSeconds"]!.GetValue<int>());
        Assert.Equal(expected: 300, docling["ConversionStallTimeoutSeconds"]!.GetValue<int>());
    }

    [Fact]
    public void DeliberateCustomBudgetSurvivesUpgrade()
    {
        JsonObject settings = Settings("http://localhost:5001", conversionTimeoutSeconds: 7200);

        JsonObject patched = PatchAppSettingsTestDriver.Run(settings, doclingEndpoint: "");

        JsonObject docling = patched["DocumentIngestion"]!["Docling"]!.AsObject();
        Assert.Equal(expected: 7200, docling["ConversionTimeoutSeconds"]!.GetValue<int>());
        Assert.Equal(expected: 300, docling["ConversionStallTimeoutSeconds"]!.GetValue<int>());
    }

    private static JsonObject Settings(string endpoint, int conversionTimeoutSeconds = 14400) => new()
        {
            ["MongoDB"] = new JsonObject
                              {
                                  ["Profiles"] = new JsonObject
                                                     {
                                                         ["local"] = new JsonObject
                                                                         {
                                                                             ["ConnectionString"] = "mongodb://localhost:27017",
                                                                             ["DatabaseName"] = "SaddleRAG"
                                                                         }
                                                     }
                              },
            ["Ollama"] = new JsonObject { ["Endpoint"] = "http://localhost:11434" },
            ["Onnx"] = new JsonObject { ["ExecutionProvider"] = "Cpu" },
            ["DocumentIngestion"] = new JsonObject
                                        {
                                            ["Docling"] = new JsonObject
                                                              {
                                                                  ["Endpoint"] = endpoint,
                                                                  ["ApiKey"] = "preserve-me",
                                                                  ["StartupGracePeriodSeconds"] = 120,
                                                                  ["HealthTimeoutSeconds"] = 10,
                                                                  ["ReadinessTimeoutSeconds"] = 30,
                                                                  ["ConversionTimeoutSeconds"] = conversionTimeoutSeconds,
                                                                  ["StartupPollIntervalMilliseconds"] = 1000
                                                              }
                                        },
            ["Arbitrary"] = new JsonObject { ["Keep"] = true, ["Nested"] = "unchanged" }
        };
}
