// ExternalToolRegistry.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Reads and writes the user's external-tool registrations. Owned by the Tray, which
///     runs as the logged-in user — the MCP service runs as LocalSystem and never touches
///     this file.
/// </summary>
public sealed class ExternalToolRegistry
{
    public ExternalToolRegistry(string filePath, ILogger<ExternalToolRegistry>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        mFilePath = filePath;
        mLogger = logger;
    }

    private static readonly JsonSerializerOptions smJsonOptions = new()
                                                                  {
                                                                      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                                                                      DefaultIgnoreCondition =
                                                                          JsonIgnoreCondition.WhenWritingNull,
                                                                      WriteIndented = true
                                                                  };

    private readonly string mFilePath;
    private readonly ILogger<ExternalToolRegistry>? mLogger;

    /// <summary>The per-user registry location the Tray and installer agree on.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     RegistryDirectoryName,
                     RegistryFileName);

    /// <summary>
    ///     Loads the registrations. A missing, unreadable, or malformed file is treated as
    ///     "nothing registered" and logged at Warning — a corrupt registry must never stop
    ///     the Tray from starting.
    /// </summary>
    public ExternalToolRegistration Read()
    {
        ExternalToolRegistration result = ExternalToolRegistration.Empty;
        if (File.Exists(mFilePath))
        {
            try
            {
                string json = File.ReadAllText(mFilePath);
                result = JsonSerializer.Deserialize<ExternalToolRegistration>(json, smJsonOptions)
                         ?? ExternalToolRegistration.Empty;
            }
            catch(Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                mLogger?.LogWarning(ex, UnreadableRegistryMessage, mFilePath);
            }
        }

        return result;
    }

    /// <summary>
    ///     Persists the registrations, replacing the previous file atomically so a crash
    ///     mid-write cannot leave unparseable JSON behind.
    /// </summary>
    public void Write(ExternalToolRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        string? directory = Path.GetDirectoryName(mFilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = mFilePath + TemporarySuffix;
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(registration, smJsonOptions));
        File.Move(temporaryPath, mFilePath, overwrite: true);
    }

    private const string RegistryDirectoryName = "SaddleRAG";
    private const string RegistryFileName = "external-tools.json";
    private const string TemporarySuffix = ".tmp";
    private const string UnreadableRegistryMessage =
        "External-tool registry at {RegistryPath} is unreadable; treating it as empty";
}
