// Stage 9 acceptance coverage for Doug's separately approved startup root fix.
using System.Reflection;
using System.Xml.Linq;
using SaddleRAG.Installer.Helper;

namespace SaddleRAG.Tests.Installer;

public sealed class StartupHelperContractTests
{
    [Fact]
    public void StartAndMonitorCustomActionNeedsOnlyDedicatedHelperAndCommand()
    {
        string root = ResolveRepositoryRoot();
        XDocument package = XDocument.Load(Path.Combine(root, "SaddleRAG.Installer", "Package.wxs"));
        XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
        XElement action = package.Descendants(ns + "SetProperty")
                                 .Single(e => (string?)e.Attribute("Id") == "StartAndMonitorService");
        string command = (string?)action.Attribute("Value") ?? string.Empty;

        Assert.Equal("\"[MCPFOLDER]SaddleRAG.Installer.Helper.exe\" start-and-monitor", command);
        Assert.DoesNotContain("powershell", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh", command, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("start-and-monitor.ps1", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionDefaultsUseOwnedServiceSiblingBinaryAndBoundedTiming()
    {
        StartAndMonitorOptions options = StartAndMonitorOptions.ForProduction();

        AssertProductionDefaults(options);
    }

    [Fact]
    public void CommandWithoutOptionsUsesProductionDefaults()
    {
        StartAndMonitorOptions? options = ParseOptions(["start-and-monitor"], out string error);

        Assert.Empty(error);
        Assert.NotNull(options);
        AssertProductionDefaults(options);
    }

    [Fact]
    public void ExplicitStartupOptionsRemainSupported()
    {
        string[] arguments =
        [
            "start-and-monitor",
            "--service-name", "SaddleRAGMcp",
            "--health-url", "http://localhost:7100/health",
            "--binary-path", "C:\\SaddleRAG\\SaddleRAG.Mcp.exe",
            "--total-timeout-seconds", "120",
            "--poll-interval-seconds", "3",
            "--health-timeout-seconds", "4",
            "--max-start-attempts", "6"
        ];

        StartAndMonitorOptions? options = ParseOptions(arguments, out string error);

        Assert.Empty(error);
        Assert.NotNull(options);
        Assert.Equal("SaddleRAGMcp", options.ServiceName);
        Assert.Equal(new Uri("http://localhost:7100/health", UriKind.Absolute), options.HealthUrl);
        Assert.Equal("C:\\SaddleRAG\\SaddleRAG.Mcp.exe", options.BinaryPath);
        Assert.Equal(TimeSpan.FromSeconds(120), options.TotalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), options.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(4), options.HealthRequestTimeout);
        Assert.Equal(6, options.MaxStartAttempts);
    }

    [Fact]
    public void StartupHelperIsAWindowsSelfContainedExecutableIncludedInInstallerPayload()
    {
        string root = ResolveRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(root,
                                                        "SaddleRAG.Installer.Helper",
                                                        "SaddleRAG.Installer.Helper.csproj"));
        string xml = project.ToString(SaveOptions.DisableFormatting);
        Assert.Contains("<OutputType>Exe</OutputType>", xml, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", xml, StringComparison.Ordinal);
        Assert.Contains("<SelfContained>true</SelfContained>", xml, StringComparison.Ordinal);

        string package = File.ReadAllText(Path.Combine(root, "SaddleRAG.Installer", "Package.wxs"));
        Assert.Contains("SaddleRAG.Installer.Helper.exe", package, StringComparison.Ordinal);
    }

    [Fact]
    public void HelperCapturesChildStdoutAndStderrSeparatelyAndNeverUsesShellRedirection()
    {
        string root = ResolveRepositoryRoot();
        string helperDirectory = Path.Combine(root, "SaddleRAG.Installer.Helper");
        string source = string.Join('\n',
                                    Directory.GetFiles(helperDirectory, "*.cs", SearchOption.AllDirectories)
                                             .Select(File.ReadAllText));

        Assert.Contains("RedirectStandardOutput = true", source, StringComparison.Ordinal);
        Assert.Contains("RedirectStandardError = true", source, StringComparison.Ordinal);
        Assert.Contains("StandardOutput", source, StringComparison.Ordinal);
        Assert.Contains("StandardError", source, StringComparison.Ordinal);
        Assert.DoesNotContain("2>&1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe /c", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwsh", source, StringComparison.OrdinalIgnoreCase);

        string legacyScript = Path.Combine(root, "SaddleRAG.Mcp", "start-and-monitor.ps1");
        if (File.Exists(legacyScript))
            Assert.DoesNotContain("2>&1", File.ReadAllText(legacyScript), StringComparison.Ordinal);
    }

    [Fact]
    public void StartupHelperControlsOnlyTheSaddleRagServiceAndHasNoDoclingOrTaskSchedulerPath()
    {
        string root = ResolveRepositoryRoot();
        string helperDirectory = Path.Combine(root, "SaddleRAG.Installer.Helper");
        string source = string.Join('\n',
                                    Directory.GetFiles(helperDirectory, "*", SearchOption.AllDirectories)
                                             .Where(path => Path.GetExtension(path) is ".cs" or ".csproj")
                                             .Select(File.ReadAllText));

        Assert.Contains("SaddleRAGMcp", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Docling", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TaskScheduler", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schtasks", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Schedule.Service", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Register-ScheduledTask", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedNativeCommandReportsBothStreamsWithoutMergingThem()
    {
        var process = Substitute.For<IInstallerProcessRunner>();
        process.RunAsync(Arg.Any<ProcessInvocation>(), Arg.Any<CancellationToken>())
               .Returns(new ProcessExecutionResult(5, "ordinary output", "specific native error"));
        var command = new StartAndMonitorCommand(process,
                                                 Substitute.For<IWindowsServiceController>(),
                                                 Substitute.For<IInstallerHealthProbe>());

        StartAndMonitorResult result = await command.RunAsync(StartAndMonitorOptions.ForTests(),
                                                               TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("ordinary output", result.StandardOutput);
        Assert.Equal("specific native error", result.StandardError);
        Assert.NotEqual(result.StandardOutput, result.StandardError);
    }

    private static void AssertProductionDefaults(StartAndMonitorOptions options)
    {
        Assert.Equal("SaddleRAGMcp", options.ServiceName);
        Assert.Equal(new Uri("http://localhost:6100/health", UriKind.Absolute), options.HealthUrl);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "SaddleRAG.Mcp.exe"), options.BinaryPath);
        Assert.Equal(TimeSpan.FromSeconds(300), options.TotalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.HealthRequestTimeout);
        Assert.Equal(5, options.MaxStartAttempts);
    }

    private static StartAndMonitorOptions? ParseOptions(IReadOnlyList<string> arguments,
                                                        out string error)
    {
        MethodInfo? method = typeof(StartAndMonitorOptions).GetMethod("Parse",
                                                                      BindingFlags.NonPublic
                                                                      | BindingFlags.Static);
        Assert.NotNull(method);
        object?[] parameters = [arguments, string.Empty];
        var result = method.Invoke(null, parameters) as StartAndMonitorOptions;
        error = Assert.IsType<string>(parameters[1]);
        return result;
    }

    private static string ResolveRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "SaddleRAG.slnx")))
            current = current.Parent;
        Assert.NotNull(current);
        return current.FullName;
    }
}
