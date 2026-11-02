// ComponentCatalogReader.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;
using Microsoft.Extensions.Logging;
using SaddleRAG.Core.Hosting;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Works out where each component lives from the <c>appsettings.json</c> the SaddleRAG service
///     reads (the copy in the tray's own install folder), so the tray knows the answer even while the
///     service is down. It always returns four rows: the SaddleRAG service, MongoDB, Docling, Ollama.
///     <para>
///         The settings are read with <see cref="JsonDocument" /> rather than the configuration
///         library, accepting what the service's own JSON provider accepts: comments, trailing commas,
///         and section and key names in any letter case. The MongoDB connection is chosen by the
///         server's rule (<c>SaddleRagDbSettings.ResolveConfiguredProfileName</c>): the
///         <c>SADDLERAG_MONGODB_PROFILE</c> override, else <c>ActiveProfile</c>, selects an entry of
///         <c>Profiles</c> (matched exactly, as the server matches it); when that names no profile, the
///         direct <c>ConnectionString</c> is used. A missing or unreadable file is logged at Warning
///         and the server's defaults are used.
///     </para>
///     <para>
///         No row carries a credential: the MongoDB connection string is reduced to a port and a
///         local-or-remote flag, and credentials, query and fragment are dropped from HTTP endpoints.
///     </para>
/// </summary>
public sealed class ComponentCatalogReader
{
    /// <param name="appSettingsPath">The <c>appsettings.json</c> to read.</param>
    /// <param name="services">Finds the Windows service that runs MongoDB.</param>
    /// <param name="logger">Optional; receives a Warning when the settings cannot be read.</param>
    /// <param name="environmentReader">
    ///     Reads an environment variable by name. Defaults to the machine-level value, which is what a
    ///     Windows service sees; tests supply their own.
    /// </param>
    public ComponentCatalogReader(string appSettingsPath,
                                  IWindowsServiceLocator services,
                                  ILogger<ComponentCatalogReader>? logger = null,
                                  Func<string, string?>? environmentReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appSettingsPath);
        ArgumentNullException.ThrowIfNull(services);

        mAppSettingsPath = appSettingsPath;
        mServices = services;
        mLogger = logger;
        mEnvironmentReader = environmentReader ?? ReadMachineEnvironmentVariable;
    }

    private readonly string mAppSettingsPath;
    private readonly IWindowsServiceLocator mServices;
    private readonly ILogger<ComponentCatalogReader>? mLogger;
    private readonly Func<string, string?> mEnvironmentReader;

    /// <summary>The four component rows, in the order SaddleRAG service, MongoDB, Docling, Ollama.</summary>
    public IReadOnlyList<ComponentDefinition> Read()
    {
        using JsonDocument? document = LoadSettings();
        JsonElement root = document?.RootElement ?? default;
        IReadOnlyList<ComponentDefinition> result =
        [
            BuildSaddleRagService(),
            BuildMongoDb(root),
            BuildDocling(root),
            BuildOllama(root)
        ];
        return result;
    }

    private JsonDocument? LoadSettings()
    {
        JsonDocument? result = null;
        try
        {
            result = JsonDocument.Parse(File.ReadAllText(mAppSettingsPath), smDocumentOptions);
            if (result.RootElement.ValueKind != JsonValueKind.Object)
            {
                mLogger?.LogWarning(NotAnObjectMessage, mAppSettingsPath);
                result.Dispose();
                result = null;
            }
        }
        catch(Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            mLogger?.LogWarning(ex, UnreadableSettingsMessage, mAppSettingsPath);
        }

        return result;
    }

    private static ComponentDefinition BuildSaddleRagService()
    {
        var endpoint = new Uri(SaddleRagEndpointText);
        var result = new ComponentDefinition(ComponentKind.SaddleRagService,
                                             SaddleRagDisplayName,
                                             endpoint,
                                             endpoint.Port,
                                             HostedLocally: true,
                                             SaddleRagServiceName,
                                             ExecutablePath: null,
                                             ModelsDirectory: null);
        return result;
    }

    private ComponentDefinition BuildMongoDb(JsonElement root)
    {
        string connectionString = ResolveMongoConnectionString(Child(root, MongoSection));
        bool hostedLocally = LocalEndpoint.IsLoopbackMongoConnectionString(connectionString);
        int? port = hostedLocally ? LocalEndpoint.MongoPort(connectionString) : null;
        Uri? endpoint = port.HasValue ? new Uri($"{MongoLocalScheme}{LocalHostName}:{port.Value}") : null;
        string? serviceName = hostedLocally ? mServices.FindServiceByExecutable(MongoExecutableFileName) : null;
        var result = new ComponentDefinition(ComponentKind.MongoDb,
                                             MongoDisplayName,
                                             endpoint,
                                             port,
                                             hostedLocally,
                                             serviceName,
                                             ExecutablePath: null,
                                             ModelsDirectory: null);
        return result;
    }

    private static ComponentDefinition BuildDocling(JsonElement root)
    {
        JsonElement docling = Child(Child(root, DocumentIngestionSection), DoclingSection);
        Uri? endpoint = ParseHttpEndpoint(GetString(docling, EndpointKey) ?? DefaultDoclingEndpoint);
        var result = new ComponentDefinition(ComponentKind.Docling,
                                             DoclingDisplayName,
                                             endpoint,
                                             endpoint?.Port,
                                             IsLoopback(endpoint),
                                             WindowsServiceName: null,
                                             ExecutablePath: null,
                                             ModelsDirectory: null);
        return result;
    }

    private static ComponentDefinition BuildOllama(JsonElement root)
    {
        JsonElement ollama = Child(root, OllamaSection);
        Uri? endpoint = ParseHttpEndpoint(GetString(ollama, EndpointKey) ?? DefaultOllamaEndpoint);
        var result = new ComponentDefinition(ComponentKind.Ollama,
                                             OllamaDisplayName,
                                             endpoint,
                                             endpoint?.Port,
                                             IsLoopback(endpoint),
                                             WindowsServiceName: null,
                                             NullIfBlank(GetString(ollama, ExecutablePathKey)),
                                             NullIfBlank(GetString(ollama, ModelsDirectoryKey)));
        return result;
    }

    // Mirrors SaddleRagDbSettings.Resolve: the override (when the variable is set at all) takes
    // precedence over ActiveProfile; a name that is empty or absent from Profiles falls back to the
    // direct ConnectionString, and a missing ConnectionString to the server's default.
    private string ResolveMongoConnectionString(JsonElement mongo)
    {
        string? profileName = mEnvironmentReader(ProfileOverrideVariable) ?? GetString(mongo, ActiveProfileKey);
        JsonElement? profile = string.IsNullOrEmpty(profileName) ? null : FindProfile(mongo, profileName);
        string result = (profile.HasValue
                             ? GetString(profile.Value, ConnectionStringKey)
                             : GetString(mongo, ConnectionStringKey))
                        ?? DefaultMongoConnectionString;
        return result;
    }

    // The server's Profiles dictionary is case-sensitive, so the profile name is matched exactly.
    private static JsonElement? FindProfile(JsonElement mongo, string profileName)
    {
        JsonElement profiles = Child(mongo, ProfilesKey);
        JsonElement? result = null;
        if (profiles.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in profiles.EnumerateObject()
                                                      .Where(property => property.Value.ValueKind == JsonValueKind.Object &&
                                                                         string.Equals(property.Name,
                                                                                       profileName,
                                                                                       StringComparison.Ordinal)))
            {
                result = property.Value;
            }
        }

        return result;
    }

    // Section and key names are matched ignoring case, as the configuration system does. A parent that
    // is not an object, or a key that is absent, yields an undefined element.
    private static JsonElement Child(JsonElement parent, string name)
    {
        JsonElement result = default;
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in parent.EnumerateObject()
                                                    .Where(property => string.Equals(property.Name,
                                                                                     name,
                                                                                     StringComparison.OrdinalIgnoreCase)))
            {
                result = property.Value;
            }
        }

        return result;
    }

    private static string? GetString(JsonElement parent, string name)
    {
        JsonElement value = Child(parent, name);
        string? result = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return result;
    }

    // Only http and https endpoints are usable; credentials, query and fragment are dropped so no row can
    // carry them. Anything else (blank, relative, another scheme) means "not configured".
    private static Uri? ParseHttpEndpoint(string text)
    {
        Uri? result = null;
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            result = new UriBuilder(parsed)
                     {
                         UserName = string.Empty,
                         Password = string.Empty,
                         Query = string.Empty,
                         Fragment = string.Empty
                     }.Uri;
        }

        return result;
    }

    private static bool IsLoopback(Uri? endpoint) => endpoint is not null && LocalEndpoint.IsLoopbackUri(endpoint.AbsoluteUri);

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string? ReadMachineEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);

    private const string SaddleRagDisplayName = "SaddleRAG";
    private const string SaddleRagServiceName = "SaddleRAGMcp";
    private const string SaddleRagEndpointText = "http://localhost:6100";
    private const string MongoDisplayName = "MongoDB";
    private const string DoclingDisplayName = "Docling";
    private const string OllamaDisplayName = "Ollama";
    private const string MongoExecutableFileName = "mongod.exe";
    private const string MongoLocalScheme = "mongodb://";
    private const string LocalHostName = "localhost";
    private const string DefaultMongoConnectionString = "mongodb://localhost:27017";
    private const string DefaultDoclingEndpoint = "http://localhost:5001";
    private const string DefaultOllamaEndpoint = "http://localhost:11434";

    // Same name as SaddleRagDbSettings.ProfileEnvVar, which is internal to the Database project.
    private const string ProfileOverrideVariable = "SADDLERAG_MONGODB_PROFILE";

    private const string MongoSection = "MongoDB";
    private const string DocumentIngestionSection = "DocumentIngestion";
    private const string DoclingSection = "Docling";
    private const string OllamaSection = "Ollama";
    private const string ActiveProfileKey = "ActiveProfile";
    private const string ProfilesKey = "Profiles";
    private const string ConnectionStringKey = "ConnectionString";
    private const string EndpointKey = "Endpoint";
    private const string ExecutablePathKey = "ExecutablePath";
    private const string ModelsDirectoryKey = "ModelsDirectory";
    private const string UnreadableSettingsMessage =
        "Component settings at {SettingsPath} are unreadable; using the default component locations";
    private const string NotAnObjectMessage =
        "Component settings at {SettingsPath} are not a JSON object; using the default component locations";

    // The service's JSON configuration provider skips comments and allows trailing commas.
    private static readonly JsonDocumentOptions smDocumentOptions = new()
                                                                    {
                                                                        CommentHandling = JsonCommentHandling.Skip,
                                                                        AllowTrailingCommas = true
                                                                    };
}
