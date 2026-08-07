// StartAndMonitorOptions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Globalization;

namespace SaddleRAG.Installer.Helper;

/// <summary>Validated arguments for the installer service startup command.</summary>
public sealed class StartAndMonitorOptions
{
    public StartAndMonitorOptions(string serviceName,
                                  Uri healthUrl,
                                  string binaryPath,
                                  TimeSpan totalTimeout,
                                  TimeSpan pollInterval,
                                  TimeSpan healthRequestTimeout,
                                  int maxStartAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(healthUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);
        if (!serviceName.Equals(OwnedServiceName, StringComparison.Ordinal))
            throw new ArgumentException($"Only the '{OwnedServiceName}' service is supported.", nameof(serviceName));
        if (!healthUrl.IsAbsoluteUri || healthUrl.Scheme is not ("http" or "https"))
            throw new ArgumentException("The health URL must be an absolute HTTP or HTTPS URL.", nameof(healthUrl));
        if (totalTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(totalTimeout), totalTimeout, "The total timeout must be positive.");
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), pollInterval, "The poll interval must be positive.");
        if (healthRequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(healthRequestTimeout), healthRequestTimeout, "The health timeout must be positive.");
        if (maxStartAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStartAttempts), maxStartAttempts, "The attempt count must be positive.");

        ServiceName = serviceName;
        HealthUrl = healthUrl;
        BinaryPath = binaryPath;
        TotalTimeout = totalTimeout;
        PollInterval = pollInterval;
        HealthRequestTimeout = healthRequestTimeout;
        MaxStartAttempts = maxStartAttempts;
    }

    public string ServiceName { get; }

    public Uri HealthUrl { get; }

    public string BinaryPath { get; }

    public TimeSpan TotalTimeout { get; }

    public TimeSpan PollInterval { get; }

    public TimeSpan HealthRequestTimeout { get; }

    public int MaxStartAttempts { get; }

    public static StartAndMonitorOptions ForTests() =>
        new(OwnedServiceName,
            new Uri(DefaultHealthUrl, UriKind.Absolute),
            DefaultBinaryName,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            maxStartAttempts: 1);

    internal static StartAndMonitorOptions? Parse(IReadOnlyList<string> arguments,
                                                  out string error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        StartAndMonitorOptions? result = null;
        error = string.Empty;
        if (arguments.Count == 0 || !arguments[0].Equals(CommandName, StringComparison.Ordinal))
        {
            error = $"Expected command '{CommandName}'.";
        }
        else
        {
            ParseValues(arguments, out ParsedValues values, out error);
            if (string.IsNullOrEmpty(error))
                result = CreateParsed(values, out error);
        }

        return result;
    }

    private static void ParseValues(IReadOnlyList<string> arguments,
                                    out ParsedValues values,
                                    out string error)
    {
        values = new ParsedValues();
        error = string.Empty;
        if ((arguments.Count - 1) % 2 != 0)
        {
            error = MissingOptionValueError;
        }
        else
        {
            for (int index = 1; index < arguments.Count && string.IsNullOrEmpty(error); index += 2)
                ApplyValue(values, arguments[index], arguments[index + 1], out error);
        }
    }

    private static void ApplyValue(ParsedValues values,
                                   string name,
                                   string value,
                                   out string error)
    {
        error = string.Empty;
        switch(name)
        {
            case ServiceNameOption:
                values.ServiceName = value;
                break;
            case HealthUrlOption:
                values.HealthUrl = value;
                break;
            case BinaryPathOption:
                values.BinaryPath = value;
                break;
            case TotalTimeoutOption:
                values.TotalTimeoutSeconds = ParsePositive(value, name, out error);
                break;
            case PollIntervalOption:
                values.PollIntervalSeconds = ParsePositive(value, name, out error);
                break;
            case HealthTimeoutOption:
                values.HealthTimeoutSeconds = ParsePositive(value, name, out error);
                break;
            case MaxStartAttemptsOption:
                values.MaxStartAttempts = ParsePositive(value, name, out error);
                break;
            default:
                error = $"Unknown option '{name}'.";
                break;
        }
    }

    private static int ParsePositive(string value, string name, out string error)
    {
        var parsed = int.TryParse(value,
                                  NumberStyles.None,
                                  CultureInfo.InvariantCulture,
                                  out int candidate)
                     && candidate > 0;
        int result = parsed ? candidate : 0;
        error = parsed ? string.Empty : $"Option '{name}' requires a positive integer.";
        return result;
    }

    private static StartAndMonitorOptions? CreateParsed(ParsedValues values,
                                                        out string error)
    {
        StartAndMonitorOptions? result = null;
        error = string.Empty;
        var healthValid = Uri.TryCreate(values.HealthUrl, UriKind.Absolute, out Uri? healthUrl);
        if (string.IsNullOrWhiteSpace(values.ServiceName)
            || string.IsNullOrWhiteSpace(values.BinaryPath)
            || !healthValid
            || healthUrl == null
            || values.TotalTimeoutSeconds <= 0
            || values.PollIntervalSeconds <= 0
            || values.HealthTimeoutSeconds <= 0
            || values.MaxStartAttempts <= 0)
        {
            error = InvalidRequiredOptionsError;
        }
        else
        {
            try
            {
                result = new StartAndMonitorOptions(values.ServiceName,
                                                    healthUrl,
                                                    values.BinaryPath,
                                                    TimeSpan.FromSeconds(values.TotalTimeoutSeconds),
                                                    TimeSpan.FromSeconds(values.PollIntervalSeconds),
                                                    TimeSpan.FromSeconds(values.HealthTimeoutSeconds),
                                                    values.MaxStartAttempts);
            }
            catch(ArgumentException ex)
            {
                error = ex.Message;
            }
        }

        return result;
    }

    private sealed class ParsedValues
    {
        public string ServiceName { get; set; } = string.Empty;
        public string HealthUrl { get; set; } = string.Empty;
        public string BinaryPath { get; set; } = string.Empty;
        public int TotalTimeoutSeconds { get; set; }
        public int PollIntervalSeconds { get; set; }
        public int HealthTimeoutSeconds { get; set; }
        public int MaxStartAttempts { get; set; }
    }

    public const string OwnedServiceName = "SaddleRAGMcp";

    private const string CommandName = "start-and-monitor";
    private const string ServiceNameOption = "--service-name";
    private const string HealthUrlOption = "--health-url";
    private const string BinaryPathOption = "--binary-path";
    private const string TotalTimeoutOption = "--total-timeout-seconds";
    private const string PollIntervalOption = "--poll-interval-seconds";
    private const string HealthTimeoutOption = "--health-timeout-seconds";
    private const string MaxStartAttemptsOption = "--max-start-attempts";
    private const string DefaultHealthUrl = "http://localhost:6100/health";
    private const string DefaultBinaryName = "SaddleRAG.Mcp.exe";
    private const string MissingOptionValueError = "Every option must have a value.";
    private const string InvalidRequiredOptionsError = "Required startup options are missing or invalid.";
}
