// StartupFailureReporter.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Connections;

namespace SaddleRAG.Mcp;

/// <summary>
///     Detects Kestrel port-bind failures and writes a high-visibility banner to
///     stderr identifying the port and the process holding it. The default
///     ASP.NET Core failure surface is a one-line ERR followed by a multi-page
///     stack trace, which is easy to miss in console output. The banner is
///     written after the framework log so it lands at the bottom of the
///     failure output, where the operator's eye lands first.
/// </summary>
internal static class StartupFailureReporter
{
    #region ListenerOwner type

    public readonly record struct ListenerOwner(int Pid, string ProcessName, DateTime? StartTime);

    #endregion


    #region Detection

    /// <summary>
    ///     Walks the exception chain looking for <see cref="AddressInUseException" />.
    ///     Kestrel wraps the socket failure in an <see cref="IOException" />, so the
    ///     marker may be one or more levels deep.
    /// </summary>
    public static bool IsPortBindFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        bool res = false;
        Exception? cur = ex;
        while (cur != null && !res)
        {
            if (cur is AddressInUseException)
                res = true;
            cur = cur.InnerException;
        }
        return res;
    }


    /// <summary>
    ///     Walks the exception chain for a "host:port" fragment in any message
    ///     (Kestrel includes it in the outer <see cref="IOException" />). Falls
    ///     back to <paramref name="fallbackPort" /> when nothing parseable is found.
    /// </summary>
    public static int ExtractPort(Exception ex, int fallbackPort)
    {
        ArgumentNullException.ThrowIfNull(ex);

        int port = fallbackPort;
        bool found = false;
        Exception? cur = ex;
        while (cur != null && !found)
        {
            var match = smPortPattern.Match(cur.Message);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed))
            {
                port = parsed;
                found = true;
            }
            cur = cur.InnerException;
        }
        return port;
    }

    #endregion


    #region Banner

    public static void WriteBanner(int port)
    {
        var owner = FindListenerOwner(port);
        var lines = BuildBannerLines(port, owner);

        var prevColor = Console.ForegroundColor;
        try
        {
            Console.Error.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            foreach (var line in lines)
                Console.Error.WriteLine(line);
            Console.Error.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = prevColor;
        }
    }


    private static IEnumerable<string> BuildBannerLines(int port, ListenerOwner? owner)
    {
        yield return Border;
        yield return BannerHeaderLine;
        yield return $"  Port {port} (http://127.0.0.1:{port}) is already in use.";

        string ownerLine = owner switch
        {
            { StartTime: { } start } o =>
                $"  Held by PID {o.Pid}  {o.ProcessName}  (started {start:HH:mm:ss})",
            { } o =>
                $"  Held by PID {o.Pid}  {o.ProcessName}",
            _ =>
                BannerNoOwnerLine
        };
        yield return ownerLine;

        yield return BannerFixLine1;
        yield return BannerFixLine2;
        yield return Border;
    }

    #endregion


    #region Listener owner lookup

    public static ListenerOwner? FindListenerOwner(int port)
    {
        ListenerOwner? res = null;
        if (OperatingSystem.IsWindows())
            res = FindListenerOwnerWindows(port);
        return res;
    }


    private static ListenerOwner? FindListenerOwnerWindows(int port)
    {
        ListenerOwner? res = null;
        int bufferSize = ProbeTcpTableBufferSize();
        if (bufferSize > 0)
            res = ReadOwnerFromTable(bufferSize, port);
        return res;
    }


    private static int ProbeTcpTableBufferSize()
    {
        int bufferSize = 0;
        GetExtendedTcpTable(IntPtr.Zero,
                            ref bufferSize,
                            sort: false,
                            AfInet,
                            TcpTableOwnerPidListener,
                            reserved: 0
                           );
        return bufferSize;
    }


    private static ListenerOwner? ReadOwnerFromTable(int bufferSize, int port)
    {
        ListenerOwner? res = null;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            int size = bufferSize;
            int rc = GetExtendedTcpTable(buffer,
                                         ref size,
                                         sort: false,
                                         AfInet,
                                         TcpTableOwnerPidListener,
                                         reserved: 0
                                        );
            if (rc == NoError)
                res = ResolveOwnerForPort(buffer, port);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return res;
    }


    private static ListenerOwner? ResolveOwnerForPort(IntPtr table, int port)
    {
        ListenerOwner? res = null;
        int pid = LocatePidForPort(table, port);
        if (pid > 0)
            res = LookupProcess(pid);
        return res;
    }


    private static int LocatePidForPort(IntPtr table, int port)
    {
        int found = 0;
        int rowCount = Marshal.ReadInt32(table);
        int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
        // First DWORD is dwNumEntries; rows begin immediately after it.
        IntPtr cursor = IntPtr.Add(table, sizeof(int));
        int i = 0;
        while (i < rowCount && found == 0)
        {
            var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(cursor);
            int rowPort = NetworkToHostPort(row.pmLocalPort);
            if (rowPort == port)
                found = (int) row.pmOwningPid;
            cursor = IntPtr.Add(cursor, rowSize);
            i++;
        }
        return found;
    }


    private static int NetworkToHostPort(uint dwLocalPort)
    {
        // dwLocalPort holds the 16-bit port number in network byte order in its
        // low two bytes — swap them to get the host-order port value.
        int hi = (int) (dwLocalPort & PortHiByteMask) << PortByteShift;
        int lo = (int) (dwLocalPort >> PortByteShift) & PortLoByteMask;
        return hi | lo;
    }


    private static ListenerOwner? LookupProcess(int pid)
    {
        ListenerOwner? res = null;
        try
        {
            using var p = Process.GetProcessById(pid);
            DateTime? startTime = null;
            try
            {
                startTime = p.StartTime;
            }
            catch(Exception)
            {
                // StartTime can throw for protected/system processes; the rest
                // of the diagnostic is still useful so we swallow this one.
            }
            res = new ListenerOwner(pid, p.ProcessName, startTime);
        }
        catch(ArgumentException)
        {
            // Process exited between table read and lookup.
        }
        catch(InvalidOperationException)
        {
        }
        return res;
    }

    #endregion


    #region Win32 interop

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint pmState;
        public uint pmLocalAddr;
        public uint pmLocalPort;
        public uint pmRemoteAddr;
        public uint pmRemotePort;
        public uint pmOwningPid;
    }


    [DllImport(IpHelperApi, SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr pTcpTable,
                                                  ref int dwOutBufLen,
                                                  bool sort,
                                                  int ipVersion,
                                                  int tblClass,
                                                  int reserved
                                                 );

    #endregion


    #region Constants and statics

    private const string Border = "================================================================";
    private const string BannerHeaderLine = "  SADDLERAG.MCP FAILED TO START";
    private const string BannerNoOwnerLine = "  (could not identify the holding process)";
    private const string BannerFixLine1 = "  Fix: stop the other process, or change";
    private const string BannerFixLine2 = "       Kestrel:Endpoints:Http:Port in appsettings.json";
    private const string IpHelperApi = "iphlpapi.dll";
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int NoError = 0;
    private const int PortByteShift = 8;
    private const uint PortHiByteMask = 0xFF;
    private const int PortLoByteMask = 0xFF;

    private static readonly Regex smPortPattern = new Regex(@":(\d+):\s*address",
                                                            RegexOptions.Compiled | RegexOptions.IgnoreCase
                                                           );

    #endregion
}
