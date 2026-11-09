// LocalEndpoint.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

namespace SaddleRAG.Core.Hosting;

/// <summary>
///     Decides whether a connection string points only at this machine.
///     SaddleRAG may restart a stopped local MongoDB service, but must never
///     act on a service when the database is on another machine.
/// </summary>
public static class LocalEndpoint
{
    /// <summary>
    ///     True only when the scheme is <c>mongodb://</c> and every host in the
    ///     host list is <c>localhost</c>, <c>127.0.0.1</c> or <c>::1</c>.
    ///     <c>mongodb+srv://</c> is always remote. Parsed without the MongoDB driver,
    ///     and the credentials are discarded without being inspected or returned.
    /// </summary>
    public static bool IsLoopbackMongoConnectionString(string? connectionString)
    {
        bool result = false;
        if (!string.IsNullOrWhiteSpace(connectionString) &&
            connectionString.StartsWith(MongoScheme, StringComparison.OrdinalIgnoreCase))
        {
            string afterScheme = connectionString.Substring(MongoScheme.Length);
            int hostListEnd = afterScheme.IndexOfAny(smHostListTerminators);
            string authorityAndHosts = hostListEnd < 0 ? afterScheme : afterScheme.Substring(0, hostListEnd);
            string hostList = authorityAndHosts.Substring(authorityAndHosts.LastIndexOf(CredentialSeparator) + 1);
            string[] hosts = hostList.Split(HostSeparator);
            result = hosts.All(host => IsLoopbackHost(StripPort(host)));
        }

        return result;
    }

    private static string StripPort(string hostAndPort)
    {
        string host = hostAndPort.Trim();
        string result;
        if (host.StartsWith(IPv6Open))
        {
            int close = host.IndexOf(IPv6Close);
            result = close < 0 ? host : host.Substring(1, close - 1);
        }
        else
        {
            int colon = host.IndexOf(PortSeparator);
            result = colon < 0 ? host : host.Substring(0, colon);
        }

        return result;
    }

    private static bool IsLoopbackHost(string host) =>
        smLoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    private const string MongoScheme = "mongodb://";
    private const char CredentialSeparator = '@';
    private const char HostSeparator = ',';
    private const char PortSeparator = ':';
    private const char IPv6Open = '[';
    private const char IPv6Close = ']';
    private static readonly char[] smHostListTerminators = ['/', '?'];
    private const string LocalhostName = "localhost";
    private const string IPv4Loopback = "127.0.0.1";
    private const string IPv6Loopback = "::1";
    private static readonly string[] smLoopbackHosts = [LocalhostName, IPv4Loopback, IPv6Loopback];
}
