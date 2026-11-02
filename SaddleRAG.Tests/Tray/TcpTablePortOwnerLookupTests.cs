// TcpTablePortOwnerLookupTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using SaddleRAG.Tray.Services;

#endregion

namespace SaddleRAG.Tests.Tray;

/// <summary>Reads the real Windows listener table, so it skips off Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class TcpTablePortOwnerLookupTests
{
    private const string WindowsOnlySkipReason = "GetExtendedTcpTable exists only on Windows.";

    [Fact]
    public void FindsThisProcessOnAnIpv4Listener()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint) listener.LocalEndpoint).Port;

            ListeningProcess? owner = new TcpTablePortOwnerLookup().FindListeningProcess(port);

            Assert.NotNull(owner);
            Assert.Equal(Environment.ProcessId, owner.ProcessId);
            using Process self = Process.GetCurrentProcess();
            Assert.Equal(self.ProcessName, owner.ProcessName);
            Assert.False(string.IsNullOrEmpty(owner.ExecutablePath));
            Assert.Equal(self.StartTime.ToUniversalTime(), owner.StartTimeUtc);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void FindsThisProcessOnAnIpv6Listener()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);
        if (!Socket.OSSupportsIPv6)
            Assert.Skip("IPv6 is not available on this computer.");

        var listener = new TcpListener(IPAddress.IPv6Loopback, port: 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint) listener.LocalEndpoint).Port;

            ListeningProcess? owner = new TcpTablePortOwnerLookup().FindListeningProcess(port);

            Assert.NotNull(owner);
            Assert.Equal(Environment.ProcessId, owner.ProcessId);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void ReturnsNullForAClosedPort()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        int port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();

        ListeningProcess? owner = new TcpTablePortOwnerLookup().FindListeningProcess(port);

        Assert.Null(owner);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void RefusesAPortOutsideTheValidRange(int port)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip(WindowsOnlySkipReason);

        Assert.Throws<ArgumentOutOfRangeException>(() => new TcpTablePortOwnerLookup().FindListeningProcess(port));
    }
}