// McpSkillsTestHost.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using SaddleRAG.Mcp;

#endregion

namespace SaddleRAG.Tests.Mcp;

/// <summary>
///     Runs the real MCP transport on a temporary loopback port without production startup services.
/// </summary>
internal sealed class McpSkillsTestHost : IAsyncDisposable
{
    private const string ModernProtocolVersion = "2026-07-28";
    private const string ProtocolVersionMetadata = "io.modelcontextprotocol/protocolVersion";
    private const string CapabilitiesMetadata = "io.modelcontextprotocol/clientCapabilities";
    private const string EventDataPrefix = "data: ";
    private readonly WebApplication mApplication;
    private readonly HttpClient mClient;

    private McpSkillsTestHost(WebApplication application, string address)
    {
        mApplication = application;
        mClient = new HttpClient
                      {
                          BaseAddress = new Uri(address),
                          Timeout = TimeSpan.FromSeconds(20)
                      };
    }

    public string Origin => mClient.BaseAddress?.GetLeftPart(UriPartial.Authority)
                            ?? throw new InvalidOperationException("The test server has no base address.");

    public static async Task<McpSkillsTestHost> StartAsync(
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development"
        });
        builder.Configuration.Sources.Clear();
        if (configuration is not null)
            builder.Configuration.AddInMemoryCollection(configuration);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(static options => options.Listen(IPAddress.Loopback, port: 0));
        builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation
        {
            Name = "SaddleRAG transport test",
            Version = "1.0.0"
        })
               .WithHttpTransport(static options => options.Stateless = true)
               .WithSaddleRagSkills()
               .WithListPromptsHandler(static (_, _) => ValueTask.FromResult(SaddleRagPrompts.List()))
               .WithGetPromptHandler(static (request, _) =>
                                         ValueTask.FromResult(SaddleRagPrompts.Get(request.Params?.Name)));

        WebApplication application = builder.Build();
        application.UseMiddleware<McpOriginValidationMiddleware>();
        application.MapMcp("/mcp");
        McpSkillsTestHost result;
        try
        {
            using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
            await application.StartAsync(cancellation.Token);
            string address = application.Services.GetRequiredService<IServer>()
                                        .Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                             ?? throw new InvalidOperationException("The test server did not publish an address.");
            result = new McpSkillsTestHost(application, address);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
        return result;
    }

    public async Task<McpSkillsTestResponse> SendAsync(
        string method,
        JsonObject? parameters = null,
        string protocolVersion = ModernProtocolVersion,
        string? origin = null,
        string? host = null,
        bool includeRoutingName = true,
        string? methodHeader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolVersion);

        JsonObject requestParameters = parameters?.DeepClone().AsObject() ?? new JsonObject();
        if (protocolVersion == ModernProtocolVersion && !requestParameters.ContainsKey("_meta"))
        {
            requestParameters["_meta"] = new JsonObject
            {
                [ProtocolVersionMetadata] = ModernProtocolVersion,
                [CapabilitiesMetadata] = new JsonObject()
            };
        }

        JsonObject requestBody = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method,
            ["params"] = requestParameters
        };
        using HttpRequestMessage request = new(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", protocolVersion);
        request.Headers.Add("Mcp-Method", methodHeader ?? method);
        string? routingName = (requestParameters["uri"] as JsonValue)?.TryGetValue(out string? uri) == true
                                  ? uri
                                  : (requestParameters["name"] as JsonValue)?.GetValue<string>();
        if (includeRoutingName && routingName is not null)
            request.Headers.Add("Mcp-Name", routingName);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (host is not null)
            request.Headers.Host = host;

        using HttpResponseMessage response = await mClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        JsonObject? message = ParseMessage(body, response.Content.Headers.ContentType?.MediaType);
        return new McpSkillsTestResponse(response.StatusCode,
                                        message,
                                        response.Headers.Contains("Mcp-Session-Id"));
    }

    private static JsonObject? ParseMessage(string body, string? mediaType)
    {
        JsonObject? result = null;
        if (mediaType == "application/json" && !string.IsNullOrWhiteSpace(body))
            result = JsonNode.Parse(body)?.AsObject();
        if (mediaType == "text/event-stream")
        {
            foreach (string line in body.Split('\n'))
            {
                JsonObject? candidate = ParseEventMessage(line);
                if (candidate is not null)
                    result = candidate;
            }
        }
        return result;
    }

    private static JsonObject? ParseEventMessage(string line)
    {
        JsonObject? result = null;
        if (line.StartsWith(EventDataPrefix, StringComparison.Ordinal))
        {
            JsonObject? candidate = JsonNode.Parse(line[EventDataPrefix.Length..])?.AsObject();
            if (candidate?.ContainsKey("id") == true)
                result = candidate;
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        mClient.Dispose();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(20));
        try
        {
            await mApplication.StopAsync(cancellation.Token);
        }
        finally
        {
            await mApplication.DisposeAsync();
        }
    }
}
