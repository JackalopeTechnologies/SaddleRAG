// SaddleRagSkillsTransportTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SaddleRAG.ClientIntegration.Skills;

#endregion

namespace SaddleRAG.Tests.Mcp;

public sealed class SaddleRagSkillsTransportTests
{
    private const string SkillUri = "skill://saddlerag-query/SKILL.md";
    private const string SkillsExtension = "io.modelcontextprotocol/skills";
    private const string LegacyProtocolVersion = "2025-11-25";
    private const int InvalidParamsCode = -32602;
    private const int MethodNotFoundCode = -32601;

    [Fact]
    public async Task DiscoveryAdvertisesSkillsAndResourcesWithoutCreatingASession()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync("server/discover");

        JsonObject result = GetResult(response);
        Assert.False(response.HasSessionId);
        Assert.Contains("2026-07-28", GetArray(result["supportedVersions"]).Select(GetString));
        Assert.NotNull(result["capabilities"]?["resources"]);
        Assert.NotNull(result["capabilities"]?["extensions"]?[SkillsExtension]);
        Assert.NotEqual(true, result["capabilities"]?["extensions"]?[SkillsExtension]?["directoryRead"]?.GetValue<bool>());
        AssertCacheFields(result);
    }

    [Fact]
    public async Task EveryListedSkillCanBeFetchedAndReadWithMatchingManifestBytes()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();
        JsonObject listing = GetResult(await host.SendAsync("skills/list"));
        AssertCacheFields(listing);
        JsonArray skills = GetArray(listing["skills"]);
        Assert.Equal(SkillCatalog.All.Count, skills.Count);

        foreach (JsonNode? entry in skills)
        {
            JsonObject skill = Assert.IsType<JsonObject>(entry);
            string uri = GetString(skill["uri"]);
            JsonObject fetched = GetResult(await host.SendAsync("skills/get", new JsonObject { ["uri"] = uri }));
            Assert.True(JsonNode.DeepEquals(skill, fetched["skill"]));
            AssertCacheFields(fetched);
            JsonArray resources = GetArray(skill["resources"]);
            Assert.Contains(resources, resource => resource?["uri"]?.GetValue<string>() == uri);

            foreach (JsonNode? resource in resources)
            {
                JsonObject file = Assert.IsType<JsonObject>(resource);
                string resourceUri = GetString(file["uri"]);
                JsonObject read = GetResult(await host.SendAsync("resources/read", new JsonObject { ["uri"] = resourceUri }));
                AssertCacheFields(read);
                JsonObject content = Assert.IsType<JsonObject>(Assert.Single(GetArray(read["contents"])));
                Assert.Equal(resourceUri, content["uri"]?.GetValue<string>());
                string text = GetString(content["text"]);
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                Assert.Equal(GetLong(file["size"]), bytes.LongLength);
                Assert.Equal(GetString(file["digest"]),
                             "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
                if (resourceUri == uri)
                {
                    Assert.Equal("text/markdown", content["mimeType"]?.GetValue<string>());
                    Assert.StartsWith("---", text, StringComparison.Ordinal);
                    JsonObject frontmatter = Assert.IsType<JsonObject>(skill["frontmatter"]);
                    string name = GetString(frontmatter["name"]);
                    Assert.Equal($"skill://{name}/SKILL.md", uri);
                    Assert.False(string.IsNullOrWhiteSpace(GetString(frontmatter["description"])));
                }
            }
        }
    }

    [Fact]
    public async Task SkillGetDoesNotRequireAnUnspecifiedRoutingHeader()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync("skills/get",
                                                            new JsonObject { ["uri"] = SkillUri },
                                                            includeRoutingName: false);

        Assert.Equal(SkillUri, GetResult(response)["skill"]?["uri"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("skills/get", "skill://missing/SKILL.md")]
    [InlineData("skills/get", "file:///C:/Windows/win.ini")]
    [InlineData("skills/get", "skill://saddlerag-query/../saddlerag-first/SKILL.md")]
    [InlineData("resources/read", "skill://saddlerag-query/unknown.md")]
    [InlineData("resources/read", "file:///C:/Windows/win.ini")]
    public async Task UnknownOrUnservedUrisReturnInvalidParams(string method, string uri)
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync(method, new JsonObject { ["uri"] = uri });

        Assert.Equal(InvalidParamsCode, response.Message?["error"]?["code"]?.GetValue<int>());
        Assert.Null(response.Message?["result"]);
    }

    [Theory]
    [InlineData("skills/get")]
    public async Task MissingUriReturnsInvalidParams(string method)
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync(method);

        Assert.Equal(InvalidParamsCode, response.Message?["error"]?["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task NonStringSkillUriReturnsInvalidParams()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync("skills/get", new JsonObject { ["uri"] = 7 });

        Assert.Equal(InvalidParamsCode, response.Message?["error"]?["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task LegacyMetadataCannotEnableModernSkillsMethods()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();
        JsonObject parameters = new()
        {
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject()
            }
        };

        McpSkillsTestResponse response = await host.SendAsync("skills/list", parameters, LegacyProtocolVersion);

        Assert.Equal(MethodNotFoundCode, response.Message?["error"]?["code"]?.GetValue<int>());
        Assert.Null(response.Message?["result"]);
    }

    [Fact]
    public async Task LegacyInitializationPromptsAndResourcesRemainReadable()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();
        JsonObject initializeParameters = new()
        {
            ["protocolVersion"] = LegacyProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "legacy-test", ["version"] = "1.0.0" }
        };

        JsonObject initialized = GetResult(await host.SendAsync("initialize", initializeParameters, LegacyProtocolVersion));
        Assert.Equal(LegacyProtocolVersion, initialized["protocolVersion"]?.GetValue<string>());
        JsonObject prompts = GetResult(await host.SendAsync("prompts/list", protocolVersion: LegacyProtocolVersion));
        Assert.Equal(SkillCatalog.All.Count, prompts["prompts"]?.AsArray().Count);
        Assert.Null(prompts["resultType"]);
        JsonObject prompt = GetResult(await host.SendAsync("prompts/get",
                                                          new JsonObject { ["name"] = "saddlerag-query" },
                                                          LegacyProtocolVersion));
        Assert.NotEmpty(GetArray(prompt["messages"]));
        JsonObject resources = GetResult(await host.SendAsync("resources/list", protocolVersion: LegacyProtocolVersion));
        int expectedResources = SkillCatalog.All.Sum(static skill => SkillCatalog.GetFiles(skill.Name).Count);
        Assert.Equal(expectedResources, resources["resources"]?.AsArray().Count);
        JsonObject read = GetResult(await host.SendAsync("resources/read",
                                                        new JsonObject { ["uri"] = SkillUri },
                                                        LegacyProtocolVersion));
        Assert.NotEmpty(GetArray(read["contents"]));
        Assert.Null(read["resultType"]);
    }

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("null")]
    [InlineData("http://localhost.attacker.example")]
    [InlineData("http://user@localhost")]
    [InlineData("http://localhost/untrusted-path")]
    [InlineData("http://localhost?query=untrusted")]
    [InlineData("http://localhost#fragment")]
    public async Task HostileOriginsAreRejectedBeforeDispatch(string origin)
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync("skills/list", origin: origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Message?["result"]);
    }

    [Fact]
    public async Task MatchingSpoofedHostAndOriginDoNotBypassLocalOriginPolicy()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync("skills/list",
                                                            origin: "http://attacker.example",
                                                            host: "attacker.example");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SameLoopbackOriginIsAccepted()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync("skills/list", origin: host.Origin);

        Assert.NotEmpty(GetArray(GetResult(response)["skills"]));
    }

    [Fact]
    public async Task ExplicitlyConfiguredOriginIsAccepted()
    {
        Dictionary<string, string?> configuration = new()
        {
            ["Mcp:AllowedOrigins:0"] = "https://trusted.example"
        };
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync(configuration);

        McpSkillsTestResponse response = await host.SendAsync("skills/list", origin: "https://trusted.example");

        Assert.NotEmpty(GetArray(GetResult(response)["skills"]));
    }

    [Fact]
    public async Task MissingResourceRoutingHeaderIsRejected()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync("resources/read",
                                                            new JsonObject { ["uri"] = SkillUri },
                                                            includeRoutingName: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32020, response.Message?["error"]?["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task MismatchedMethodHeaderIsRejected()
    {
        await using McpSkillsTestHost host = await McpSkillsTestHost.StartAsync();

        McpSkillsTestResponse response = await host.SendAsync("skills/list", methodHeader: "skills/get");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32020, response.Message?["error"]?["code"]?.GetValue<int>());
    }

    private static JsonObject GetResult(McpSkillsTestResponse response)
    {
        string diagnostic = $"HTTP {(int)response.StatusCode}: {response.Message?.ToJsonString() ?? "No JSON response"}";
        Assert.True(response.StatusCode == HttpStatusCode.OK, diagnostic);
        Assert.True(response.Message?["error"] is null, diagnostic);
        return Assert.IsType<JsonObject>(response.Message?["result"]);
    }

    private static void AssertCacheFields(JsonObject result)
    {
        Assert.Equal("complete", result["resultType"]?.GetValue<string>());
        Assert.True(result["ttlMs"]?.GetValue<long>() >= 0);
        Assert.Contains(result["cacheScope"]?.GetValue<string>(), new[] { "public", "private" });
    }

    private static JsonArray GetArray(JsonNode? node)
    {
        return Assert.IsType<JsonArray>(node);
    }

    private static string GetString(JsonNode? node)
    {
        Assert.NotNull(node);
        return node.GetValue<string>();
    }

    private static long GetLong(JsonNode? node)
    {
        Assert.NotNull(node);
        return node.GetValue<long>();
    }
}
