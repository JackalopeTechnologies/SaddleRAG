// SaddleRagDataProtection.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using Microsoft.AspNetCore.DataProtection;

namespace SaddleRAG.Mcp;

/// <summary>Separates disposable installer prewarm keys from normal host key storage.</summary>
internal static class SaddleRagDataProtection
{
    /// <summary>Preserves normal Development key storage without creating it for prewarm.</summary>
    public static void ConfigureDevelopment(IServiceCollection services,
                                             IHostEnvironment environment,
                                             bool prewarmMode,
                                             string localApplicationDataPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(localApplicationDataPath);

        if (!prewarmMode && environment.IsDevelopment())
            ConfigureDevelopmentStorage(services, environment.EnvironmentName, localApplicationDataPath);
    }

    /// <summary>Isolates disposable keys after all normal service registrations, immediately before host construction.</summary>
    public static void IsolatePrewarm(IServiceCollection services, bool prewarmMode)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (prewarmMode)
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();

            // ASP.NET Core 10's ephemeral-provider extension replaces only IDataProtectionProvider.
            // Its separate hosted service still preloads persistent keys unless removed here.
            // Match the exact framework type and assembly; retain all other hosted services.
            // Real-host startup tests guard this framework implementation dependency.
            ServiceDescriptor[] preloaders = services.Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType is { } implementation &&
                implementation.Assembly == typeof(EphemeralDataProtectionProvider).Assembly &&
                implementation.FullName == DataProtectionHostedServiceType).ToArray();
            if (preloaders.Length != 1)
                throw new InvalidOperationException(UnexpectedPreloaderRegistration);
            services.Remove(preloaders[0]);
        }
    }

    private static void ConfigureDevelopmentStorage(IServiceCollection services,
                                                     string environmentName,
                                                     string localApplicationDataPath)
    {
        string directory = Path.Combine(localApplicationDataPath,
                                         AppName,
                                         DataProtectionKeysSubdirectory,
                                         environmentName);
        Directory.CreateDirectory(directory);
        IDataProtectionBuilder builder = services.AddDataProtection()
                                                 .SetApplicationName($"{McpApplicationName}.{environmentName}")
                                                 .PersistKeysToFileSystem(new DirectoryInfo(directory));
        if (OperatingSystem.IsWindows())
            builder.ProtectKeysWithDpapi();
    }

    private const string AppName = "SaddleRAG";
    private const string McpApplicationName = "SaddleRAG.Mcp";
    private const string DataProtectionKeysSubdirectory = "DataProtection-Keys";
    private const string DataProtectionHostedServiceType =
        "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService";
    private const string UnexpectedPreloaderRegistration =
        "The expected ASP.NET Core key-loading service was not registered exactly once; prewarm cannot safely isolate persistent keys.";
}
