// SaddleRagSkillsExtensions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SaddleRAG.Mcp;

/// <summary>Registers the official Skills extension alongside the standard resource methods.</summary>
internal static class SaddleRagSkillsExtensions
{
    public static IMcpServerBuilder WithSaddleRagSkills(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Configure<McpServerOptions>(ConfigureSkills);
        builder.WithListResourcesHandler(static (request, cancellationToken) =>
                                             ValueTask.FromResult(SaddleRagSkills.ListResources(request.Params?.Cursor)))
               .WithReadResourceHandler(static (request, cancellationToken) =>
                                            ValueTask.FromResult(SaddleRagSkills.ReadResource(request.Params?.Uri)))
               .WithListResourceTemplatesHandler(static (request, cancellationToken) =>
                                                     ValueTask.FromResult(new ListResourceTemplatesResult { ResourceTemplates = [] }));
        return builder;
    }

    private static void ConfigureSkills(McpServerOptions options)
    {
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Resources ??= new ResourcesCapability();
        options.Capabilities.Extensions ??= new Dictionary<string, object>();
        options.Capabilities.Extensions[SaddleRagSkills.ExtensionName] = new JsonObject();

        // SDK 2.3.0 has no typed Skills extension API. Its only custom-method registration
        // API is experimental; isolate this third-party constraint to these registrations.
#pragma warning disable MCPEXP002
        options.RequestHandlers ??= [];
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = SkillProtocolNames.ListMethod, Handler = SaddleRagSkills.ListAsync });
        options.RequestHandlers.Add(new McpServerRequestHandler { Method = SkillProtocolNames.GetMethod, Handler = SaddleRagSkills.GetAsync });
#pragma warning restore MCPEXP002
    }
}
