// Stage 9 acceptance coverage.
// Proposed production seams are intentionally referenced before they exist.

using System.Text.Json.Nodes;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Mcp;
using SaddleRAG.Mcp.Tools;

namespace SaddleRAG.Tests.Mcp;

public sealed class DoclingHealthToolsTests
{
    public static TheoryData<DoclingCapabilityStatus> UnavailableStatuses => new()
        {
            Status(DoclingReasonCodes.EndpointUnreachable, "connection refused"),
            Status(DoclingReasonCodes.HealthTimeout, "health probe timed out"),
            Status(DoclingReasonCodes.Unauthorized, "endpoint rejected credentials"),
            Status(DoclingReasonCodes.HealthInvalid, "health payload was unhealthy"),
            Status(DoclingReasonCodes.ApiIncompatible, "required conversion API was absent"),
            Status(DoclingReasonCodes.ModelsUnavailable, "models were unavailable"),
            Status(DoclingReasonCodes.ConversionFailed, "owned conversion probe failed")
        };

    [Theory]
    [MemberData(nameof(UnavailableStatuses))]
    public async Task GetDocumentIngestionStatusPreservesObservedDiagnostic(DoclingCapabilityStatus observed)
    {
        var capability = Substitute.For<IDoclingCapabilityService>();
        capability.GetStatusAsync(true, Arg.Any<CancellationToken>()).Returns(observed);

        string json = await DocumentIngestionTools.GetDocumentIngestionStatus(
                          capability,
                          refresh: true,
                          TestContext.Current.CancellationToken);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(observed.State.ToString(), root["State"]!.GetValue<string>());
        Assert.Equal(observed.ReasonCode, root["ReasonCode"]!.GetValue<string>());
        Assert.Equal(observed.Detail, root["Detail"]!.GetValue<string>());
        Assert.Equal(observed.Endpoint, root["Endpoint"]!.GetValue<string>());
        Assert.Equal(observed.LastCheckedAt, root["LastCheckedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(observed.Remediation, root["Remediation"]!.GetValue<string>());
        Assert.Equal(new[] { "Detail", "Endpoint", "LastCheckedAt", "ReasonCode", "Remediation", "State" },
                     root.Select(property => property.Key)
                         .OrderBy(key => key, StringComparer.Ordinal)
                         .ToArray());
        await capability.Received(1).GetStatusAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDoclingInstallInstructionsIsDocumentationOnlyAndResolvesConfiguredHealthUrl()
    {
        var settings = new DoclingSettings { Endpoint = "http://127.0.0.1:8123/base/" };

        string json = await DocumentIngestionTools.GetDoclingInstallInstructions(settings);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("1.29.0", root["CompatibilityTestedVersion"]!.GetValue<string>());
        Assert.Equal(DoclingInstallInstructions.OfficialInstallUrl,
                     root["OfficialInstallUrl"]!.GetValue<string>());
        Assert.Equal(DoclingInstallInstructions.OfficialReleaseUrl,
                     root["OfficialReleaseUrl"]!.GetValue<string>());
        Assert.Equal(DoclingInstallInstructions.OfficialApiUrl,
                     root["OfficialApiUrl"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:8123/base/health",
                     root["HealthTestUrl"]!.GetValue<string>());

        string instructions = root["Instructions"]!.GetValue<string>();
        Assert.Contains("PYTHONUTF8=1", instructions, StringComparison.Ordinal);
        Assert.Contains("TORCH_COMPILE_DISABLE=1", instructions, StringComparison.Ordinal);
        Assert.Contains("startup task", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user", root["OwnershipNotice"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        Assert.Null(root["Installed"]);
        Assert.Null(root["Started"]);
        Assert.Null(root["LicenseAccepted"]);
    }

    [Fact]
    public void UnavailableDoclingDoesNotChangeCoreHealthStatus()
    {
        DoclingCapabilityStatus unavailable = Status(DoclingReasonCodes.EndpointUnreachable,
                                                      "No connection could be made.");
        var capability = Substitute.For<IDoclingCapabilityService>();
        capability.CurrentStatus.Returns(unavailable);
        var warmup = new McpWarmupState();

        ServiceHealthResponse response = ServiceHealthResponseFactory.Create("Healthy", warmup, capability);

        Assert.Equal("Healthy", response.Status);
        Assert.Equal("Unavailable", response.DocumentIngestion.Status);
        Assert.Equal(unavailable.ReasonCode, response.DocumentIngestion.ReasonCode);
        Assert.Equal(unavailable.Detail, response.DocumentIngestion.Detail);
        Assert.Equal(unavailable.Endpoint, response.DocumentIngestion.Endpoint);
        Assert.Equal(unavailable.LastCheckedAt, response.DocumentIngestion.LastCheckedAt);
        Assert.Equal(unavailable.Remediation, response.DocumentIngestion.Remediation);
    }

    [Fact]
    public void HealthRouteAlwaysWrapsCoreAndOptionalCapabilityPayloadInHttpOk()
    {
        string root = ResolveRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(root, "SaddleRAG.Mcp", "Program.cs"));
        int route = program.IndexOf("app.MapGet(HealthEndpointPath", StringComparison.Ordinal);
        Assert.True(route >= 0);
        string healthBlock = program.Substring(route, Math.Min(900, program.Length - route));

        Assert.Contains("Results.Ok", healthBlock, StringComparison.Ordinal);
        Assert.Contains("ServiceHealthResponseFactory.Create", healthBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Results.StatusCode", healthBlock, StringComparison.Ordinal);
    }

    private static DoclingCapabilityStatus Status(string reasonCode, string detail) =>
        new(DoclingCapabilityState.Unavailable,
            reasonCode,
            detail,
            "http://localhost:5001",
            new DateTimeOffset(2026, 8, 4, 15, 30, 0, TimeSpan.Zero),
            "Install or repair the user-managed Docling endpoint, then test it again.");

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "SaddleRAG.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
        return current.FullName;
    }
}
