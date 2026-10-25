// PrewarmDataProtectionTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SaddleRAG.Mcp;

namespace SaddleRAG.Tests.Mcp;

public sealed class PrewarmDataProtectionTests : IDisposable
{
    private readonly string mLocalData = Path.Combine(Path.GetTempPath(), TestDirectory, Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    [InlineData("Development", false)]
    public async Task PrewarmNeverTouchesAnExplicitRepositoryDuringHostStartupOrProtection(
        string environmentName, bool registerRepositoryFirst)
    {
        var repository = Substitute.For<IXmlRepository>();
        repository.GetAllElements().Returns(_ => throw new InvalidOperationException(RepositoryMustNotBeRead));
        repository.When(value => value.StoreElement(Arg.Any<XElement>(), Arg.Any<string>()))
                  .Do(_ => throw new InvalidOperationException(RepositoryMustNotBeWritten));
        var otherService = Substitute.For<IHostedService>();
        using IHost host = CreateHost(environmentName, true, registerRepositoryFirst, repository, otherService);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var provider = Assert.IsType<EphemeralDataProtectionProvider>(
            host.Services.GetRequiredService<IDataProtectionProvider>());
        IDataProtector protector = provider.CreateProtector(Purpose);
        Assert.Equal(Payload, protector.Unprotect(protector.Protect(Payload)));
        await host.StopAsync(TestContext.Current.CancellationToken);

        repository.DidNotReceive().GetAllElements();
        repository.DidNotReceive().StoreElement(Arg.Any<XElement>(), Arg.Any<string>());
        await otherService.Received(1).StartAsync(Arg.Any<CancellationToken>());
        await otherService.Received(1).StopAsync(Arg.Any<CancellationToken>());
        Assert.False(Directory.Exists(mLocalData));
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    [InlineData("Development", false)]
    public async Task NormalHostRetainsRepositoryPreloadingAndPersistentProtection(
        string environmentName, bool registerRepositoryFirst)
    {
        var elements = new List<XElement>();
        var repository = Substitute.For<IXmlRepository>();
        repository.GetAllElements().Returns(_ => elements.Select(element => new XElement(element)).ToList());
        repository.When(value => value.StoreElement(Arg.Any<XElement>(), Arg.Any<string>()))
                  .Do(call => elements.Add(new XElement(call.Arg<XElement>())));
        var otherService = Substitute.For<IHostedService>();
        using IHost first = CreateHost(environmentName, false, registerRepositoryFirst, repository, otherService);

        await first.StartAsync(TestContext.Current.CancellationToken);
        repository.Received().GetAllElements();
        repository.Received().StoreElement(Arg.Any<XElement>(), Arg.Any<string>());
        Assert.Same(repository, first.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
        IDataProtectionProvider provider = first.Services.GetRequiredService<IDataProtectionProvider>();
        Assert.IsNotType<EphemeralDataProtectionProvider>(provider);
        string protectedPayload = provider.CreateProtector(Purpose).Protect(Payload);
        await first.StopAsync(TestContext.Current.CancellationToken);

        using IHost second = CreateHost(environmentName, false, registerRepositoryFirst, repository,
                                        Substitute.For<IHostedService>());
        await second.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Payload, second.Services.GetRequiredService<IDataProtectionProvider>()
                                    .CreateProtector(Purpose).Unprotect(protectedPayload));
        await second.StopAsync(TestContext.Current.CancellationToken);
        await otherService.Received(1).StartAsync(Arg.Any<CancellationToken>());
        await otherService.Received(1).StopAsync(Arg.Any<CancellationToken>());
        Assert.Equal(environmentName == Environments.Development, Directory.Exists(mLocalData));
    }

    [Fact]
    public async Task DevelopmentRetainsItsApplicationDirectoryAndPlatformKeyEncryption()
    {
        using IHost host = CreateHost(Environments.Development, false, false, null, Substitute.For<IHostedService>());
        string expectedPath = Path.Combine(mLocalData, AppName, KeyDirectory, Environments.Development);
        KeyManagementOptions options = host.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var repository = Assert.IsType<FileSystemXmlRepository>(options.XmlRepository);
        Assert.Equal(expectedPath, repository.Directory.FullName);
        Assert.Equal(DevelopmentApplication,
                     host.Services.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
        if (OperatingSystem.IsWindows())
            Assert.IsType<DpapiXmlEncryptor>(options.XmlEncryptor);
        else
            Assert.Null(options.XmlEncryptor);

        await host.StartAsync(TestContext.Current.CancellationToken);
        IDataProtector protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        Assert.Equal(Payload, protector.Unprotect(protector.Protect(Payload)));
        Assert.NotEmpty(Directory.EnumerateFiles(expectedPath));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private IHost CreateHost(string environmentName,
                              bool prewarmMode,
                              bool registerRepositoryFirst,
                              IXmlRepository? repository,
                              IHostedService otherService)
    {
        IHost host = new HostBuilder().UseEnvironment(environmentName)
                                     .ConfigureLogging(logging => logging.ClearProviders())
                                     .ConfigureServices((context, services) =>
                                         ConfigureServices(context.HostingEnvironment, services, prewarmMode,
                                                           registerRepositoryFirst, repository, otherService))
                                     .Build();
        return host;
    }

    private void ConfigureServices(IHostEnvironment environment,
                                     IServiceCollection services,
                                     bool prewarmMode,
                                     bool registerRepositoryFirst,
                                     IXmlRepository? repository,
                                     IHostedService otherService)
    {
        services.AddSingleton(otherService);
        SaddleRagDataProtection.ConfigureDevelopment(services, environment, prewarmMode, mLocalData);
        if (registerRepositoryFirst)
            ConfigureRepository(services, repository);
        services.AddDataProtection();
        if (!registerRepositoryFirst)
            ConfigureRepository(services, repository);
        SaddleRagDataProtection.IsolatePrewarm(services, prewarmMode);
    }

    private static void ConfigureRepository(IServiceCollection services, IXmlRepository? repository)
    {
        if (repository != null)
            services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
    }

    public void Dispose()
    {
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), TestDirectory));
        Assert.Equal(expectedParent, Directory.GetParent(mLocalData)?.FullName);
        if (Directory.Exists(mLocalData))
            Directory.Delete(mLocalData, recursive: true);
    }

    private const string TestDirectory = "SaddleRAG-prewarm-key-tests";
    private const string AppName = "SaddleRAG";
    private const string KeyDirectory = "DataProtection-Keys";
    private const string DevelopmentApplication = "SaddleRAG.Mcp.Development";
    private const string Purpose = "prewarm-test";
    private const string Payload = "Disposable host test payload";
    private const string RepositoryMustNotBeRead = "Prewarm must not read persistent keys.";
    private const string RepositoryMustNotBeWritten = "Prewarm must not write persistent keys.";
}
