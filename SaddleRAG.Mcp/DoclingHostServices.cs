// DoclingHostServices.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SaddleRAG.Ingestion.Documents.Docling;

#endregion

namespace SaddleRAG.Mcp;

/// <summary>Registers the optional, user-operated Docling capability.</summary>
public static class DoclingHostServices
{
    public static IServiceCollection AddDoclingDocumentIngestion(this IServiceCollection services,
                                                                 IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DoclingSettings>()
                .Bind(configuration.GetSection(DoclingSettings.SectionName));
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<DoclingSettings>>().Value);
        services.AddSingleton<DoclingDocumentMapper>();
        services.AddHttpClient<IDoclingClient, DoclingClient>(client =>
                                                                  client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<DoclingReadinessProbe>();
        services.AddSingleton<DoclingCapabilityService>();
        services.AddSingleton<IDoclingCapabilityService>(provider =>
            provider.GetRequiredService<DoclingCapabilityService>());
        services.AddHostedService<DoclingStartupProbeService>();
        return services;
    }

    internal const string HttpClientName = nameof(IDoclingClient);
}
