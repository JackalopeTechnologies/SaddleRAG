// RegistryWindowsServiceLocator.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.IO;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Finds the Windows service that runs a given program by reading each service's <c>ImagePath</c>
///     under <c>HKLM\SYSTEM\CurrentControlSet\Services</c>, which ordinary users can read. It only
///     reads values; the decision about which program an <c>ImagePath</c> names is
///     <see cref="ServiceImagePath" />, the same rule the elevated restart script applies.
///     If several services match, the first in ordinal name order is returned and the rest are named
///     in a Warning, because the script would accept any of them and the operator should know.
/// </summary>
public sealed class RegistryWindowsServiceLocator : IWindowsServiceLocator
{
    public RegistryWindowsServiceLocator(ILogger<RegistryWindowsServiceLocator>? logger = null)
    {
        mLogger = logger;
    }

    private readonly ILogger<RegistryWindowsServiceLocator>? mLogger;

    public string? FindServiceByExecutable(string executableFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableFileName);

        string? result = null;
        try
        {
            using RegistryKey? servicesKey = Registry.LocalMachine.OpenSubKey(ServicesRegistryPath, writable: false);
            if (servicesKey is not null)
            {
                KeyValuePair<string, string?>[] services = servicesKey.GetSubKeyNames()
                                                                      .Select(name => new KeyValuePair<string, string?>(name, ReadImagePath(servicesKey, name)))
                                                                      .ToArray();
                IReadOnlyList<string> matches = ServiceImagePath.ServicesRunning(services, executableFileName);
                result = matches.Count > 0 ? matches[0] : null;
                if (matches.Count > 1)
                    mLogger?.LogWarning(SeveralServicesMessage, executableFileName, string.Join(NameSeparator, matches), result);
            }
        }
        catch(Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            mLogger?.LogWarning(ex, UnreadableServicesMessage, ServicesRegistryPath);
        }

        return result;
    }

    private static string? ReadImagePath(RegistryKey servicesKey, string serviceName)
    {
        string? result = null;
        try
        {
            using RegistryKey? serviceKey = servicesKey.OpenSubKey(serviceName, writable: false);
            // The stored text, not an expansion with the tray's environment: the elevated script reads
            // it the same way, so the two can never disagree about which program a service runs.
            result = serviceKey?.GetValue(ImagePathValueName,
                                          defaultValue: null,
                                          RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch(Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // A single service key this user cannot read (or that vanished while listing) cannot be
            // proven to run the program, so it is simply not a candidate; the other services still count.
            result = null;
        }

        return result;
    }

    private const string ServicesRegistryPath = @"SYSTEM\CurrentControlSet\Services";
    private const string ImagePathValueName = "ImagePath";
    private const string NameSeparator = ", ";
    private const string SeveralServicesMessage =
        "More than one Windows service runs {Executable}: {Services}. Using {Chosen}";
    private const string UnreadableServicesMessage =
        "The Windows services list at {RegistryPath} could not be read; no service was identified";
}
