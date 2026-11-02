// TcpTablePortOwnerLookup.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

#endregion

namespace SaddleRAG.Tray.Services;

/// <summary>
///     Finds the process listening on a TCP port by reading Windows' listener table
///     (<c>GetExtendedTcpTable</c> with <c>TCP_TABLE_OWNER_PID_LISTENER</c>), IPv4 first, then IPv6.
///     The tray uses it to tell which process to stop when it did not start that process itself.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TcpTablePortOwnerLookup : IPortOwnerLookup
{
    public ListeningProcess? FindListeningProcess(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, MinPort);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, MaxPort);

        // IPv6 is only consulted when IPv4 has no listener, so a dual-stack listener costs one read.
        int ownerId = FindOwner(port, AddressFamilyInet4, Inet4RowSize, Inet4PortOffset, Inet4OwnerOffset);
        if (ownerId == NoOwner)
            ownerId = FindOwner(port, AddressFamilyInet6, Inet6RowSize, Inet6PortOffset, Inet6OwnerOffset);

        ListeningProcess? result = ownerId == NoOwner ? null : Describe(ownerId);
        return result;
    }

    private static int FindOwner(int port, uint addressFamily, int rowSize, int portOffset, int ownerOffset)
    {
        int owner = NoOwner;
        IntPtr table = ReadTable(addressFamily, out int tableSize);
        try
        {
            int entryCount = Marshal.ReadInt32(table);
            // Never read past what Windows reported it wrote, whatever the entry count claims.
            int readable = Math.Min(entryCount, Math.Max(val1: 0, (tableSize - EntryCountSize) / rowSize));
            for (var index = 0; index < readable && owner == NoOwner; index++)
            {
                int row = EntryCountSize + (index * rowSize);
                if (ReadPort(table, row + portOffset) == port)
                    owner = Marshal.ReadInt32(table, row + ownerOffset);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }

        return owner;
    }

    /// <summary>
    ///     Allocates and fills the listener table. The caller frees it. The size is asked for first
    ///     and the read retried if the table grows between the two calls.
    /// </summary>
    private static IntPtr ReadTable(uint addressFamily, out int tableSize)
    {
        uint size = 0;
        uint error = GetExtendedTcpTable(IntPtr.Zero, ref size, bOrder: false, addressFamily, TcpTableOwnerPidListener, reserved: 0);
        IntPtr buffer = IntPtr.Zero;
        var attempts = 0;
        while (error == ErrorInsufficientBuffer && attempts < MaxReadAttempts)
        {
            Marshal.FreeHGlobal(buffer);
            buffer = Marshal.AllocHGlobal((int) size);
            error = GetExtendedTcpTable(buffer, ref size, bOrder: false, addressFamily, TcpTableOwnerPidListener, reserved: 0);
            attempts++;
        }

        if (error != NoError)
        {
            Marshal.FreeHGlobal(buffer);
            throw new Win32Exception((int) error);
        }

        tableSize = (int) size;
        return buffer;
    }

    private static int ReadPort(IntPtr table, int offset)
    {
        // The port is a network-byte-order 16-bit value in the low half of a 32-bit field.
        var raw = (ushort) (Marshal.ReadInt32(table, offset) & PortMask);
        return BinaryPrimitives.ReverseEndianness(raw);
    }

    private static ListeningProcess? Describe(int processId)
    {
        ListeningProcess? result = null;
        try
        {
            using Process process = Process.GetProcessById(processId);
            result = new ListeningProcess(processId, process.ProcessName, ReadExecutablePath(process), ReadStartTimeUtc(process));
        }
        catch(Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The process exited between the table read and now: nothing is listening any more.
            result = null;
        }

        return result;
    }

    private static DateTime? ReadStartTimeUtc(Process process)
    {
        DateTime? startUtc = null;
        try
        {
            startUtc = process.StartTime.ToUniversalTime();
        }
        catch(Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Windows refuses the start time of another user's or an elevated process, and the
            // process may have exited. Without it the stopper will not touch the process.
            startUtc = null;
        }

        return startUtc;
    }

    private static string? ReadExecutablePath(Process process)
    {
        string? path = null;
        try
        {
            path = process.MainModule?.FileName;
        }
        catch(Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Windows refuses module access to another user's or an elevated process, and the
            // process may have exited. The path is best effort, so it stays null.
            path = null;
        }

        return path;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(TcpLibrary, ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable,
                                                   ref uint size,
                                                   [MarshalAs(UnmanagedType.Bool)] bool bOrder,
                                                   uint addressFamily,
                                                   int tableClass,
                                                   uint reserved);

    private const string TcpLibrary = "iphlpapi.dll";
    private const int MinPort = 1;
    private const int MaxPort = 65535;
    private const int NoOwner = -1;
    private const uint NoError = 0;
    private const uint ErrorInsufficientBuffer = 122;
    private const int MaxReadAttempts = 5;
    private const uint AddressFamilyInet4 = 2;
    private const uint AddressFamilyInet6 = 23;
    private const int TcpTableOwnerPidListener = 3;
    private const int PortMask = 0xFFFF;
    private const int EntryCountSize = 4;

    // MIB_TCPROW_OWNER_PID: state, local address, local port, remote address, remote port, owning pid (six DWORDs).
    private const int Inet4RowSize = 24;
    private const int Inet4PortOffset = 8;
    private const int Inet4OwnerOffset = 20;

    // MIB_TCP6ROW_OWNER_PID: local address (16 bytes), local scope id, local port, remote address (16 bytes),
    // remote scope id, remote port, state, owning pid.
    private const int Inet6RowSize = 56;
    private const int Inet6PortOffset = 20;
    private const int Inet6OwnerOffset = 52;
}