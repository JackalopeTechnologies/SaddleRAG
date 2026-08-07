// DoclingHostContractTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SaddleRAG.Ingestion.Documents.Docling;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Documents.Docling;

public sealed class DoclingHostContractTests
{
    [Fact]
    public async Task InvalidOptionalConfigurationDoesNotFailHostStartup()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                                                        {
                                                            [$"{DoclingSettings.SectionName}:Endpoint"] = "not a URI"
                                                        });
        builder.Services.AddDoclingDocumentIngestion(builder.Configuration);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var settings = host.Services.GetRequiredService<IOptions<DoclingSettings>>().Value;
        Assert.Equal("not a URI", settings.Endpoint);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TypedClientDisablesTheGlobalHttpTimeout()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient(DoclingHostServices.HttpClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void HostRegistrationProvidesOneSharedDoclingPipeline()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<DoclingDocumentMapper>());
        Assert.IsType<DoclingClient>(provider.GetRequiredService<IDoclingClient>());
        Assert.NotNull(provider.GetRequiredService<DoclingReadinessProbe>());
        var concrete = provider.GetRequiredService<DoclingCapabilityService>();
        Assert.Same(concrete, provider.GetRequiredService<IDoclingCapabilityService>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is DoclingStartupProbeService);
    }

    [Fact]
    public async Task StartupProbeRunsOnceAndRecordsUnexpectedFailureWithoutFailing()
    {
        var capability = new ThrowingCapabilityService();
        var service = new DoclingStartupProbeService(capability,
                                                     NullLogger<DoclingStartupProbeService>.Instance);

        await service.ProbeOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 1, capability.RefreshCalls);
        Assert.Equal(expected: 1, capability.RecordFailureCalls);
    }

    [Fact]
    public void HealthResponsePreservesCoreHealthAndUsesCachedDocumentStatus()
    {
        var warmup = new McpWarmupState();
        warmup.MarkStarted("Database");
        var status = ReadyStatus();
        var capability = new StaticCapabilityService(status);

        var response = ServiceHealthResponseFactory.Create("Healthy", warmup, capability);

        Assert.Equal("Healthy", response.Status);
        Assert.Equal(warmup.Status, response.WarmupStatus);
        Assert.Equal(warmup.CurrentPhase, response.WarmupPhase);
        Assert.Equal(DoclingCapabilityState.Ready.ToString(), response.DocumentIngestion.Status);
        Assert.Equal(DoclingReasonCodes.Ready, response.DocumentIngestion.ReasonCode);
        Assert.Equal(status.Endpoint, response.DocumentIngestion.Endpoint);
        Assert.Equal(expected: 0, capability.RefreshCalls);
    }

    [Fact]
    public async Task ConcurrentRefreshesShareOneKnownDocumentConversion()
    {
        var client = new GatedDoclingClient();
        var probe = new DoclingReadinessProbe(client, new DoclingSettings());
        var capability = new DoclingCapabilityService(probe);

        var first = capability.GetStatusAsync(refresh: true, TestContext.Current.CancellationToken);
        await client.HealthStarted.Task.WaitAsync(smGateTimeout, TestContext.Current.CancellationToken);
        var second = capability.GetStatusAsync(refresh: true, TestContext.Current.CancellationToken);
        client.ReleaseHealth();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(DoclingCapabilityState.Ready, result.State));
        Assert.Equal(expected: 1, client.HealthCalls);
        Assert.Equal(expected: 1, client.ReadinessCalls);
        Assert.Equal(expected: 1, client.ConversionCalls);
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDoclingDocumentIngestion(new ConfigurationBuilder().Build());
        return services;
    }

    private static DoclingCapabilityStatus ReadyStatus() =>
        new(DoclingCapabilityState.Ready,
            DoclingReasonCodes.Ready,
            "Known conversion succeeded.",
            DoclingSettings.DefaultEndpoint,
            new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero),
            "No action is required.");

    private sealed class ThrowingCapabilityService : IDoclingCapabilityService
    {
        public int RecordFailureCalls { get; private set; }

        public int RefreshCalls { get; private set; }

        public DoclingCapabilityStatus CurrentStatus { get; } = ReadyStatus();

        public Task<DoclingCapabilityStatus> GetStatusAsync(bool refresh = false,
                                                            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromException<DoclingCapabilityStatus>(new InvalidOperationException("Probe failed."));
        }

        public void RecordUnexpectedFailure() => RecordFailureCalls++;
    }

    private sealed class StaticCapabilityService : IDoclingCapabilityService
    {
        public StaticCapabilityService(DoclingCapabilityStatus status)
        {
            CurrentStatus = status;
        }

        public int RefreshCalls { get; private set; }

        public DoclingCapabilityStatus CurrentStatus { get; }

        public Task<DoclingCapabilityStatus> GetStatusAsync(bool refresh = false,
                                                            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(CurrentStatus);
        }

        public void RecordUnexpectedFailure()
        {
        }
    }

    private sealed class GatedDoclingClient : IDoclingClient
    {
        public int ConversionCalls { get; private set; }

        public int HealthCalls { get; private set; }

        public int ReadinessCalls { get; private set; }

        public TaskCompletionSource HealthStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DoclingServiceObservation> CheckHealthAsync(
            CancellationToken cancellationToken = default)
        {
            HealthCalls++;
            HealthStarted.TrySetResult();
            await mHealthRelease.Task.WaitAsync(cancellationToken);
            return DoclingServiceObservation.Success();
        }

        public Task<DoclingServiceObservation> CheckReadinessAsync(
            CancellationToken cancellationToken = default)
        {
            ReadinessCalls++;
            return Task.FromResult(DoclingServiceObservation.Success());
        }

        public Task<DoclingConversionResult> ConvertAsync(DoclingFile file,
                                                          CancellationToken cancellationToken = default)
        {
            ConversionCalls++;
            var document = new DoclingMappedDocument(file.FileName,
                                                     DoclingProbeDocument.Marker,
                                                     DoclingProbeDocument.Marker,
                                                     "{}",
                                                     "{}",
                                                     []);
            return Task.FromResult(DoclingConversionResult.Success(document));
        }

        public void ReleaseHealth() => mHealthRelease.TrySetResult();

        private readonly TaskCompletionSource mHealthRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly TimeSpan smGateTimeout = TimeSpan.FromSeconds(value: 5);
}
