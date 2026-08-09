// DoclingSettingsTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Tests.Documents.Docling;

public sealed class DoclingSettingsTests
{
    [Fact]
    public void DefaultsMatchLocalUserManagedServiceContract()
    {
        var settings = new DoclingSettings();

        Assert.Equal("http://localhost:5001", settings.Endpoint);
        Assert.Equal(expected: 120, settings.StartupGracePeriodSeconds);
        Assert.Equal(expected: 14400, settings.ConversionTimeoutSeconds);
        Assert.Equal(expected: 300, settings.ConversionStallTimeoutSeconds);
        Assert.Equal(expected: 5000, settings.ConversionPollIntervalMilliseconds);
        Assert.Equal(expected: 2000, settings.ConversionResultRetryBaseMilliseconds);
        Assert.Empty(settings.ApiKey);
        Assert.True(settings.Validate().IsValid);
    }

    [Fact]
    public void NonPositiveStallTimeoutIsInvalid()
    {
        var settings = new DoclingSettings { ConversionStallTimeoutSeconds = 0 };

        var validation = settings.Validate();

        Assert.False(validation.IsValid);
        Assert.Equal(DoclingReasonCodes.InvalidEndpoint, validation.ReasonCode);
    }

    [Fact]
    public void StalledReasonCodeIsDistinctFromTimeout()
    {
        Assert.Equal("DOCLING_CONVERSION_STALLED", DoclingReasonCodes.ConversionStalled);
        Assert.NotEqual(DoclingReasonCodes.ConversionTimeout, DoclingReasonCodes.ConversionStalled);
    }

    [Fact]
    public void BlankEndpointIsNotConfigured()
    {
        var settings = new DoclingSettings { Endpoint = "  " };

        var validation = settings.Validate();

        Assert.False(validation.IsValid);
        Assert.Equal(DoclingReasonCodes.NotConfigured, validation.ReasonCode);
        Assert.Null(validation.Endpoint);
    }

    [Theory]
    [InlineData("not a uri")]
    [InlineData("ftp://localhost:5001")]
    [InlineData("/relative")]
    public void InvalidEndpointHasStableReasonCode(string endpoint)
    {
        var settings = new DoclingSettings { Endpoint = endpoint };

        var validation = settings.Validate();

        Assert.False(validation.IsValid);
        Assert.Equal(DoclingReasonCodes.InvalidEndpoint, validation.ReasonCode);
    }

    [Theory]
    [InlineData(0, 10, 30, 600, 1000, 5000, 2000)]
    [InlineData(120, 0, 30, 600, 1000, 5000, 2000)]
    [InlineData(120, 10, 0, 600, 1000, 5000, 2000)]
    [InlineData(120, 10, 30, 0, 1000, 5000, 2000)]
    [InlineData(120, 10, 30, 600, 0, 5000, 2000)]
    [InlineData(120, 10, 30, 600, 1000, 0, 2000)]
    [InlineData(120, 10, 30, 600, 1000, 5000, 0)]
    [InlineData(120, 10, 30, 600, 1000, 60001, 2000)]
    [InlineData(120, 10, 30, 600, 1000, 5000, 60001)]
    public void InvalidTimeoutConfigurationIsRejected(int grace,
                                                      int health,
                                                      int readiness,
                                                      int conversion,
                                                      int startupPoll,
                                                      int conversionPoll,
                                                      int resultRetry)
    {
        var settings = new DoclingSettings
                       {
                           StartupGracePeriodSeconds = grace,
                           HealthTimeoutSeconds = health,
                           ReadinessTimeoutSeconds = readiness,
                           ConversionTimeoutSeconds = conversion,
                           StartupPollIntervalMilliseconds = startupPoll,
                           ConversionPollIntervalMilliseconds = conversionPoll,
                           ConversionResultRetryBaseMilliseconds = resultRetry
                       };

        var validation = settings.Validate();

        Assert.False(validation.IsValid);
        Assert.Equal(DoclingReasonCodes.InvalidEndpoint, validation.ReasonCode);
        Assert.Contains("greater than zero", validation.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallationGuideIsDocumentationOnlyAndVersioned()
    {
        var guide = DoclingInstallInstructions.Create(new DoclingSettings());

        Assert.Equal("1.29.0", guide.CompatibilityTestedVersion);
        Assert.Contains("docling-serve", guide.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.29.0", guide.Instructions, StringComparison.Ordinal);
        Assert.Contains("PYTHONUTF8=1", guide.Instructions, StringComparison.Ordinal);
        Assert.Contains("TORCH_COMPILE_DISABLE=1", guide.Instructions, StringComparison.Ordinal);
        Assert.Contains("Docling process", guide.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user", guide.OwnershipNotice, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://docling-project.github.io/", guide.OfficialInstallUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://github.com/docling-project/", guide.OfficialReleaseUrl, StringComparison.Ordinal);
        Assert.Contains(DoclingInstallInstructions.OfficialTesseractInstallUrl,
                        guide.Instructions,
                        StringComparison.Ordinal);
        Assert.Contains("installing Tesseract alone does not change how documents are converted",
                        guide.Instructions,
                        StringComparison.Ordinal);
        Assert.Contains("DocumentIngestion:Docling:OcrEngine", guide.Instructions, StringComparison.Ordinal);
        Assert.Contains(@"tessdata\", guide.Instructions, StringComparison.Ordinal);
        Assert.Contains("deliberately unauthenticated", guide.Instructions, StringComparison.Ordinal);
        Assert.Contains("never asks for, collects, or stores secrets",
                        guide.Instructions,
                        StringComparison.Ordinal);
        Assert.Contains("DocumentIngestion:Docling:ApiKey", guide.Instructions, StringComparison.Ordinal);
        // Narrowed, not deleted: SaddleRAG may now start a command the user registered, so the
        // claim states what remains true and keeps the service's own hands-off guarantee.
        Assert.Contains("never installs, licenses, configures, or upgrades",
                        guide.OwnershipNotice,
                        StringComparison.Ordinal);
        Assert.Contains("start a command you registered", guide.OwnershipNotice, StringComparison.Ordinal);
        Assert.Contains("MCP service never starts, stops, or restarts anything",
                        guide.OwnershipNotice,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void DoclingAdapterContainsNoLifecycleControlApiInvocation()
    {
        var sourceDirectory = Path.Combine(DoclingTestSupport.RepositoryRoot(),
                                           "SaddleRAG.Ingestion",
                                           "Documents",
                                           "Docling");
        var source = string.Join('\n', Directory.GetFiles(sourceDirectory, "*.cs").Select(File.ReadAllText));

        Assert.DoesNotContain("Process.Start(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceController", source, StringComparison.Ordinal);
        Assert.DoesNotContain("schtasks", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.Win32.TaskScheduler", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OllamaBootstrapper", source, StringComparison.Ordinal);
    }
}
