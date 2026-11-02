// LocalEndpoint.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Globalization;

#endregion

namespace SaddleRAG.Core.Hosting;

/// <summary>
///     The one rule for deciding whether a configured endpoint runs on this computer.
///     The MCP server uses it to report which components are local, and the tray uses it
///     to decide which components it may start, stop, or restart. Only three hosts count
///     as loopback: <c>localhost</c>, <c>127.0.0.1</c>, and <c>::1</c>. The rest of the
///     127.0.0.0/8 range is deliberately not accepted, so a remote or unusual address is
///     always status-only.
///     <para>
///         MongoDB connection strings are parsed here without the MongoDB driver so Core and
///         the tray stay free of package dependencies. The credentials portion is discarded
///         during parsing and is never returned or logged.
///     </para>
/// </summary>
public static class LocalEndpoint
{
    /// <summary>The port MongoDB listens on when the connection string names none.</summary>
    public const int DefaultMongoPort = 27017;

    private const string MongoScheme = "mongodb://";
    private const string LocalhostName = "localhost";
    private const string Ipv4Loopback = "127.0.0.1";
    private const string Ipv6Loopback = "::1";
    private const char CredentialSeparator = '@';
    private const char HostSeparator = ',';
    private const char PortSeparator = ':';
    private const char Ipv6Open = '[';
    private const char Ipv6Close = ']';
    private const char PathStart = '/';
    private const char OptionsStart = '?';

    // In "[host]:port" the host starts just after the opening bracket, and each of '[', ']' and ':'
    // is one character wide.
    private const int BracketedHostStart = 1;
    private const int SeparatorLength = 1;
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    /// <summary>
    ///     True only for an absolute URI whose host is <c>localhost</c>, <c>127.0.0.1</c>,
    ///     or <c>::1</c> (IPv6 brackets are ignored).
    /// </summary>
    public static bool IsLoopbackUri(string? endpoint)
    {
        bool result = false;
        if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
            result = IsLoopbackHost(uri.Host.Trim(Ipv6Open, Ipv6Close));
        return result;
    }

    /// <summary>
    ///     True only for a <c>mongodb://</c> connection string in which every host in the
    ///     host list is loopback. <c>mongodb+srv://</c> is remote by definition, and an
    ///     unparseable string is not loopback.
    /// </summary>
    public static bool IsLoopbackMongoConnectionString(string? connectionString)
    {
        HostEntry[]? hosts = ParseMongoHosts(connectionString);
        bool result = hosts is not null && hosts.All(static host => IsLoopbackHost(host.Host));
        return result;
    }

    /// <summary>
    ///     The port of the first host in a <c>mongodb://</c> connection string
    ///     (<see cref="DefaultMongoPort" /> when it names none), or null when the string
    ///     cannot be parsed.
    /// </summary>
    public static int? MongoPort(string? connectionString)
    {
        HostEntry[]? hosts = ParseMongoHosts(connectionString);
        int? result = hosts?[0].Port;
        return result;
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, LocalhostName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(host, Ipv4Loopback, StringComparison.Ordinal) ||
        string.Equals(host, Ipv6Loopback, StringComparison.Ordinal);

    // Returns null when the string is not a parseable mongodb:// URI. Otherwise returns at least
    // one host, every one of which parsed. The credentials are dropped here and go no further.
    private static HostEntry[]? ParseMongoHosts(string? connectionString)
    {
        HostEntry[]? result = null;
        if (connectionString is not null && connectionString.StartsWith(MongoScheme, StringComparison.OrdinalIgnoreCase))
        {
            string remainder = connectionString[MongoScheme.Length..];
            int authorityEnd = remainder.IndexOfAny([PathStart, OptionsStart]);
            string authority = authorityEnd < 0 ? remainder : remainder[..authorityEnd];
            string hostList = authority[(authority.LastIndexOf(CredentialSeparator) + 1)..];
            result = ParseHostList(hostList);
        }

        return result;
    }

    private static HostEntry[]? ParseHostList(string hostList)
    {
        HostEntry[]? result = null;
        if (hostList.Length > 0)
        {
            string[] parts = hostList.Split(HostSeparator);
            HostEntry[] parsed = parts.Select(ParseHostEntry)
                                      .OfType<HostEntry>()
                                      .ToArray();
            if (parsed.Length == parts.Length)
                result = parsed;
        }

        return result;
    }

    private static HostEntry? ParseHostEntry(string entry)
    {
        HostEntry? result = null;
        string trimmed = entry.Trim();
        HostText? split = trimmed.StartsWith(Ipv6Open) ? SplitBracketedHost(trimmed) : SplitPlainHost(trimmed);
        if (split is { Host.Length: > 0 } hostText)
        {
            int? port = ParsePort(hostText.PortText);
            if (port.HasValue)
                result = new HostEntry(hostText.Host, port.Value);
        }

        return result;
    }

    // "[ipv6]" or "[ipv6]:port". The brackets are the only way a host may contain colons.
    private static HostText? SplitBracketedHost(string entry)
    {
        int close = entry.IndexOf(Ipv6Close);
        bool hasHost = close > BracketedHostStart;
        string tail = hasHost ? entry[(close + SeparatorLength)..] : string.Empty;
        HostText? result = hasHost
                               ? tail switch
                                 {
                                     _ when string.IsNullOrEmpty(tail) => new HostText(entry[BracketedHostStart..close], null),
                                     _ when tail.StartsWith(PortSeparator) =>
                                         new HostText(entry[BracketedHostStart..close], tail[SeparatorLength..]),
                                     _ => null
                                 }
                               : null;
        return result;
    }

    // "host" or "host:port". An unbracketed value with extra colons keeps them in the port text,
    // fails the port parse, and is rejected.
    private static HostText? SplitPlainHost(string entry)
    {
        int colon = entry.IndexOf(PortSeparator);
        HostText result = colon < 0
                              ? new HostText(entry, null)
                              : new HostText(entry[..colon], entry[(colon + 1)..]);
        return result;
    }

    private static int? ParsePort(string? portText)
    {
        int? result = DefaultMongoPort;
        if (portText is not null)
        {
            bool valid = int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) &&
                         port is >= MinPort and <= MaxPort;
            result = valid ? port : null;
        }

        return result;
    }

    private readonly record struct HostEntry(string Host, int Port);

    private readonly record struct HostText(string Host, string? PortText);
}
