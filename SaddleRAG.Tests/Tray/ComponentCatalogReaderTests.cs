// ComponentCatalogReaderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using Microsoft.Extensions.Logging;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

public sealed class ComponentCatalogReaderTests : IDisposable
{
    public ComponentCatalogReaderTests()
    {
        mTempDir = Path.Combine(Path.GetTempPath(), $"saddlerag-component-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mTempDir);
        mSettingsPath = Path.Combine(mTempDir, "appsettings.json");
    }

    private const string InstalledSettings = """
                                             {
                                               "MongoDB": {
                                                 "ActiveProfile": "local",
                                                 "Profiles": {
                                                   "local": { "ConnectionString": "mongodb://localhost:27017", "DatabaseName": "SaddleRAG" }
                                                 }
                                               },
                                               "Ollama": {
                                                 "Endpoint": "http://localhost:11434",
                                                 "ExecutablePath": "C:\\Users\\Doug\\AppData\\Local\\Programs\\Ollama\\ollama.exe",
                                                 "ModelsDirectory": "E:\\Ollama Models"
                                               },
                                               "DocumentIngestion": { "Docling": { "Endpoint": "http://localhost:5001" } }
                                             }
                                             """;

    private const string MongoExecutable = "mongod.exe";
    private const string FakeMongoServiceName = "MongoDBFake";

    private readonly string mTempDir;
    private readonly string mSettingsPath;

    public void Dispose()
    {
        if (Directory.Exists(mTempDir))
            Directory.Delete(mTempDir, recursive: true);
    }

    private sealed class FakeLocator : IWindowsServiceLocator
    {
        public string? ServiceName { get; set; } = FakeMongoServiceName;
        public List<string> Requested { get; } = [];

        public string? FindServiceByExecutable(string executableFileName)
        {
            Requested.Add(executableFileName);
            return ServiceName;
        }
    }

    private sealed class ListLogger : ILogger<ComponentCatalogReader>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
                                EventId eventId,
                                TState state,
                                Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private IReadOnlyList<ComponentDefinition> ReadSettings(string? json,
                                                            FakeLocator? locator = null,
                                                            ListLogger? logger = null,
                                                            string? profileOverride = null)
    {
        if (json is not null)
            File.WriteAllText(mSettingsPath, json);
        var reader = new ComponentCatalogReader(mSettingsPath,
                                                locator ?? new FakeLocator(),
                                                logger,
                                                _ => profileOverride);
        return reader.Read();
    }

    private static ComponentDefinition Row(IReadOnlyList<ComponentDefinition> rows, ComponentKind kind) =>
        rows.Single(row => row.Kind == kind);

    [Fact]
    public void AlwaysReturnsFourRowsInTheAgreedOrder()
    {
        IReadOnlyList<ComponentDefinition> rows = ReadSettings(InstalledSettings);

        Assert.Equal([ComponentKind.SaddleRagService, ComponentKind.MongoDb, ComponentKind.Docling, ComponentKind.Ollama],
                     rows.Select(row => row.Kind));
    }

    [Fact]
    public void InstalledShapeGivesFourLocalRowsWithPortsAndServiceName()
    {
        FakeLocator locator = new();

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(InstalledSettings, locator);

        Assert.All(rows, row => Assert.True(row.HostedLocally));
        ComponentDefinition mongo = Row(rows, ComponentKind.MongoDb);
        Assert.Equal(27017, mongo.Port);
        Assert.Equal(FakeMongoServiceName, mongo.WindowsServiceName);
        Assert.Equal([MongoExecutable], locator.Requested);
        ComponentDefinition docling = Row(rows, ComponentKind.Docling);
        Assert.Equal(new Uri("http://localhost:5001"), docling.Endpoint);
        Assert.Equal(5001, docling.Port);
        Assert.Null(docling.WindowsServiceName);
    }

    [Fact]
    public void OllamaCarriesItsExecutablePathAndModelsDirectory()
    {
        IReadOnlyList<ComponentDefinition> rows = ReadSettings(InstalledSettings);

        ComponentDefinition ollama = Row(rows, ComponentKind.Ollama);
        Assert.Equal(new Uri("http://localhost:11434"), ollama.Endpoint);
        Assert.Equal(11434, ollama.Port);
        Assert.Equal(@"C:\Users\Doug\AppData\Local\Programs\Ollama\ollama.exe", ollama.ExecutablePath);
        Assert.Equal(@"E:\Ollama Models", ollama.ModelsDirectory);
    }

    [Fact]
    public void SaddleRagRowIsTheInstalledServiceOnPort6100()
    {
        IReadOnlyList<ComponentDefinition> rows = ReadSettings(InstalledSettings);

        ComponentDefinition service = Row(rows, ComponentKind.SaddleRagService);
        Assert.Equal("SaddleRAGMcp", service.WindowsServiceName);
        Assert.Equal(new Uri("http://localhost:6100"), service.Endpoint);
        Assert.Equal(6100, service.Port);
        Assert.True(service.HostedLocally);
    }

    [Fact]
    public void MissingSettingsFileFallsBackToDefaultsAndLogsAWarning()
    {
        ListLogger logger = new();

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(json: null, logger: logger);

        Assert.Equal(new Uri("http://localhost:5001"), Row(rows, ComponentKind.Docling).Endpoint);
        Assert.Equal(new Uri("http://localhost:11434"), Row(rows, ComponentKind.Ollama).Endpoint);
        ComponentDefinition mongo = Row(rows, ComponentKind.MongoDb);
        Assert.True(mongo.HostedLocally);
        Assert.Equal(27017, mongo.Port);
        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(mSettingsPath, message);
    }

    [Fact]
    public void MalformedSettingsFileFallsBackToDefaultsAndLogsAWarning()
    {
        ListLogger logger = new();

        IReadOnlyList<ComponentDefinition> rows = ReadSettings("{ this is not json", logger: logger);

        Assert.Equal(new Uri("http://localhost:11434"), Row(rows, ComponentKind.Ollama).Endpoint);
        Assert.Equal(27017, Row(rows, ComponentKind.MongoDb).Port);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void SettingsFileThatIsNotAnObjectFallsBackToDefaultsAndLogsAWarning()
    {
        ListLogger logger = new();

        IReadOnlyList<ComponentDefinition> rows = ReadSettings("[1, 2]", logger: logger);

        Assert.Equal(new Uri("http://localhost:5001"), Row(rows, ComponentKind.Docling).Endpoint);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public void ValidSettingsLogNothing()
    {
        ListLogger logger = new();

        ReadSettings(InstalledSettings, logger: logger);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void RemoteMongoIsNotHostedLocallyAndLeaksNothingFromTheConnectionString()
    {
        FakeLocator locator = new();
        const string settings = """
                                { "MongoDB": { "ConnectionString": "mongodb+srv://dbadmin:s3cr3tPassw0rd@cluster0.remote-host.example.net/SaddleRAG?retryWrites=true" } }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings, locator);

        ComponentDefinition mongo = Row(rows, ComponentKind.MongoDb);
        Assert.False(mongo.HostedLocally);
        Assert.Null(mongo.WindowsServiceName);
        Assert.Empty(locator.Requested);
        string everything = string.Join(Environment.NewLine, rows.Select(row => row.ToString()));
        Assert.DoesNotContain("s3cr3tPassw0rd", everything);
        Assert.DoesNotContain("dbadmin", everything);
        Assert.DoesNotContain("remote-host", everything);
    }

    [Fact]
    public void RemoteMongoWithPlainSchemeIsNotHostedLocallyAndLeaksNothing()
    {
        const string settings = """
                                { "MongoDB": { "ConnectionString": "mongodb://dbadmin:s3cr3tPassw0rd@db.remote-host.example.net:27018/SaddleRAG" } }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        ComponentDefinition mongo = Row(rows, ComponentKind.MongoDb);
        Assert.False(mongo.HostedLocally);
        string everything = string.Join(Environment.NewLine, rows.Select(row => row.ToString()));
        Assert.DoesNotContain("s3cr3tPassw0rd", everything);
        Assert.DoesNotContain("dbadmin", everything);
        Assert.DoesNotContain("remote-host", everything);
    }

    [Fact]
    public void LocalMongoWithCredentialsKeepsThePortAndLeaksNoCredentials()
    {
        const string settings = """
                                { "MongoDB": { "ConnectionString": "mongodb://dbadmin:s3cr3tPassw0rd@127.0.0.1:27018/SaddleRAG" } }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        ComponentDefinition mongo = Row(rows, ComponentKind.MongoDb);
        Assert.True(mongo.HostedLocally);
        Assert.Equal(27018, mongo.Port);
        string everything = string.Join(Environment.NewLine, rows.Select(row => row.ToString()));
        Assert.DoesNotContain("s3cr3tPassw0rd", everything);
        Assert.DoesNotContain("dbadmin", everything);
    }

    [Fact]
    public void DirectConnectionStringIsUsedWhenThereAreNoProfiles()
    {
        const string settings = """{ "MongoDB": { "ConnectionString": "mongodb://localhost:27999" } }""";

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        Assert.Equal(27999, Row(rows, ComponentKind.MongoDb).Port);
    }

    [Fact]
    public void ActiveProfileMissingFromProfilesFallsBackToTheDirectConnectionString()
    {
        const string settings = """
                                {
                                  "MongoDB": {
                                    "ActiveProfile": "company",
                                    "ConnectionString": "mongodb://localhost:27555",
                                    "Profiles": { "local": { "ConnectionString": "mongodb://localhost:27017" } }
                                  }
                                }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        Assert.Equal(27555, Row(rows, ComponentKind.MongoDb).Port);
    }

    [Fact]
    public void ActiveProfileWithoutAConnectionStringUsesTheDefaultLikeTheServer()
    {
        const string settings = """
                                { "MongoDB": { "ActiveProfile": "local", "Profiles": { "local": { "DatabaseName": "X" } } } }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        ComponentDefinition mongo = Row(rows, ComponentKind.MongoDb);
        Assert.True(mongo.HostedLocally);
        Assert.Equal(27017, mongo.Port);
    }

    [Fact]
    public void ActiveProfileSelectsTheRemoteProfileOverTheDirectString()
    {
        const string settings = """
                                {
                                  "MongoDB": {
                                    "ActiveProfile": "company",
                                    "ConnectionString": "mongodb://localhost:27017",
                                    "Profiles": {
                                      "local": { "ConnectionString": "mongodb://localhost:27017" },
                                      "company": { "ConnectionString": "mongodb://shared.remote-host.example.net:27017" }
                                    }
                                  }
                                }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        Assert.False(Row(rows, ComponentKind.MongoDb).HostedLocally);
    }

    [Fact]
    public void ProfileOverrideFromTheEnvironmentWinsOverTheActiveProfile()
    {
        const string settings = """
                                {
                                  "MongoDB": {
                                    "ActiveProfile": "local",
                                    "Profiles": {
                                      "local": { "ConnectionString": "mongodb://localhost:27017" },
                                      "other": { "ConnectionString": "mongodb://localhost:27444" }
                                    }
                                  }
                                }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings, profileOverride: "other");

        Assert.Equal(27444, Row(rows, ComponentKind.MongoDb).Port);
    }

    [Fact]
    public void UnknownProfileOverrideFallsBackToTheDirectConnectionString()
    {
        const string settings = """
                                {
                                  "MongoDB": {
                                    "ActiveProfile": "local",
                                    "ConnectionString": "mongodb://localhost:27666",
                                    "Profiles": { "local": { "ConnectionString": "mongodb://localhost:27017" } }
                                  }
                                }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings, profileOverride: "nope");

        Assert.Equal(27666, Row(rows, ComponentKind.MongoDb).Port);
    }

    [Fact]
    public void SectionAndKeyNamesAreCaseInsensitiveAndCommentsAndTrailingCommasAreAccepted()
    {
        const string settings = """
                                {
                                  // the service accepts comments and trailing commas too
                                  "mongodb": { "connectionstring": "mongodb://localhost:27123", },
                                  "OLLAMA": { "endpoint": "http://localhost:11999", },
                                }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        Assert.Equal(27123, Row(rows, ComponentKind.MongoDb).Port);
        Assert.Equal(11999, Row(rows, ComponentKind.Ollama).Port);
    }

    [Fact]
    public void RemoteOllamaAndDoclingEndpointsAreNotHostedLocally()
    {
        const string settings = """
                                {
                                  "Ollama": { "Endpoint": "http://gpu-box.example.net:11434" },
                                  "DocumentIngestion": { "Docling": { "Endpoint": "https://docling.example.net" } }
                                }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        ComponentDefinition ollama = Row(rows, ComponentKind.Ollama);
        Assert.False(ollama.HostedLocally);
        Assert.Equal(new Uri("http://gpu-box.example.net:11434"), ollama.Endpoint);
        ComponentDefinition docling = Row(rows, ComponentKind.Docling);
        Assert.False(docling.HostedLocally);
        Assert.Equal(443, docling.Port);
    }

    [Fact]
    public void EndpointCredentialsAreStrippedFromTheRow()
    {
        const string settings = """
                                { "Ollama": { "Endpoint": "http://bob:hunter2@localhost:11434/?key=abc" } }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        string everything = string.Join(Environment.NewLine, rows.Select(row => row.ToString()));
        Assert.DoesNotContain("hunter2", everything);
        Assert.DoesNotContain("bob", everything);
        Assert.DoesNotContain("abc", everything);
        Assert.Equal(11434, Row(rows, ComponentKind.Ollama).Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("localhost:5001")]
    [InlineData("ftp://localhost:5001")]
    public void BlankOrUnusableDoclingEndpointIsNotConfiguredAndNotHostedLocally(string endpoint)
    {
        string settings = $$"""{ "DocumentIngestion": { "Docling": { "Endpoint": "{{endpoint}}" } } }""";

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        ComponentDefinition docling = Row(rows, ComponentKind.Docling);
        Assert.Null(docling.Endpoint);
        Assert.Null(docling.Port);
        Assert.False(docling.HostedLocally);
    }

    [Fact]
    public void NonStringSettingValuesFallBackToDefaults()
    {
        const string settings = """
                                {
                                  "MongoDB": { "ConnectionString": 5 },
                                  "Ollama": { "Endpoint": { "nested": true }, "ExecutablePath": 7 },
                                  "DocumentIngestion": { "Docling": "oops" }
                                }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        Assert.Equal(27017, Row(rows, ComponentKind.MongoDb).Port);
        ComponentDefinition ollama = Row(rows, ComponentKind.Ollama);
        Assert.Equal(new Uri("http://localhost:11434"), ollama.Endpoint);
        Assert.Null(ollama.ExecutablePath);
        Assert.Equal(new Uri("http://localhost:5001"), Row(rows, ComponentKind.Docling).Endpoint);
    }

    [Fact]
    public void EmptyOllamaPathsBecomeNull()
    {
        const string settings = """
                                { "Ollama": { "Endpoint": "http://localhost:11434", "ExecutablePath": "", "ModelsDirectory": "  " } }
                                """;

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(settings);

        ComponentDefinition ollama = Row(rows, ComponentKind.Ollama);
        Assert.Null(ollama.ExecutablePath);
        Assert.Null(ollama.ModelsDirectory);
    }

    [Fact]
    public void MongoRowHasNoServiceNameWhenNoMongodServiceIsInstalled()
    {
        FakeLocator locator = new() { ServiceName = null };

        IReadOnlyList<ComponentDefinition> rows = ReadSettings(InstalledSettings, locator);

        ComponentDefinition mongo = Row(rows, ComponentKind.MongoDb);
        Assert.True(mongo.HostedLocally);
        Assert.Null(mongo.WindowsServiceName);
    }

    [Fact]
    public void ConstructorRejectsABlankPathAndANullLocator()
    {
        Assert.Throws<ArgumentException>(() => new ComponentCatalogReader(" ", new FakeLocator()));
        Assert.Throws<ArgumentNullException>(() => new ComponentCatalogReader(mSettingsPath, null!));
    }

    [Fact]
    public void TheDefaultDoclingRowIsALocalDoclingAtTheLaunchersDefaultAddress()
    {
        // Used by the Start Docling menu item when the component list could not be read at all, so
        // Docling stays startable at the same address the launcher then uses.
        ComponentDefinition row = ComponentCatalogReader.DefaultDoclingRow();
        Uri expected = DoclingLaunchSettings.CreateDefault().Endpoint;

        Assert.Equal(ComponentKind.Docling, row.Kind);
        Assert.Equal(expected, row.Endpoint);
        Assert.Equal(expected.Port, row.Port);
        Assert.True(row.HostedLocally);
        Assert.Null(row.WindowsServiceName);
    }

    [Fact]
    public void TheDefaultDoclingRowMatchesTheRowReadWithoutSettings()
    {
        ComponentDefinition read = Row(ReadSettings(json: null), ComponentKind.Docling);

        Assert.Equal(read, ComponentCatalogReader.DefaultDoclingRow());
    }
}
