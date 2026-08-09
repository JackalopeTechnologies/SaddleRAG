// PhysicalDirectoryEntryHandle.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SaddleRAG.Ingestion.Scanning;

/// <summary>Opens filesystem entries and derives identity from the opened handle.</summary>
internal static class PhysicalDirectoryEntryHandle
{
    internal static SafeFileHandle OpenMetadata(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        SafeFileHandle result;
        if (OperatingSystem.IsWindows())
        {
            result = CreateFile(fullPath,
                                WindowsFileAccess.ReadAttributes,
                                FileShare.Read | FileShare.Write,
                                IntPtr.Zero,
                                FileMode.Open,
                                WindowsFileFlags.BackupSemantics | WindowsFileFlags.OpenReparsePoint,
                                IntPtr.Zero);
            ThrowIfInvalid(result);
        }
        else
        {
            result = File.OpenHandle(fullPath,
                                     FileMode.Open,
                                     FileAccess.Read,
                                     FileShare.Read | FileShare.Write);
        }

        return result;
    }

    internal static SafeFileHandle OpenRead(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        return File.OpenHandle(fullPath,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.Read,
                               FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal static DirectoryEntrySnapshot Snapshot(string fullPath, SafeFileHandle handle)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        ArgumentNullException.ThrowIfNull(handle);
        string canonicalPath = Path.GetFullPath(fullPath);
        string resolvedPath = ResolvePath(handle);
        FileAttributes attributes = File.GetAttributes(handle);
        if (!PathsEqual(canonicalPath, resolvedPath))
            attributes |= FileAttributes.ReparsePoint;
        bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
        long byteLength = isDirectory ? 0 : RandomAccess.GetLength(handle);
        DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(handle);
        DirectoryEntryIdentity identity = GetIdentity(handle);
        return new DirectoryEntrySnapshot(canonicalPath,
                                          attributes,
                                          byteLength,
                                          lastWriteTimeUtc,
                                          identity,
                                          resolvedPath);
    }

    internal static bool MatchesExpected(DirectoryEntrySnapshot expected,
                                         DirectoryEntrySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(current);
        bool sameKind = expected.Attributes.HasFlag(FileAttributes.Directory)
                        == current.Attributes.HasFlag(FileAttributes.Directory);
        bool sameIdentity = expected.Identity.HasValue
                            && current.Identity.HasValue
                            && expected.Identity.Value == current.Identity.Value;
        return sameKind
               && sameIdentity
               && PathsEqual(expected.IdentityPath, current.IdentityPath)
               && PathsEqual(expected.FullPath, current.FullPath);
    }

    internal static bool PathsEqual(string left, string right)
    {
        string normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        string normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return normalizedLeft.Equals(normalizedRight, comparison);
    }

    internal static string EnumerationPath(string requestedPath, SafeFileHandle directoryHandle)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestedPath);
        ArgumentNullException.ThrowIfNull(directoryHandle);
        string result = (OperatingSystem.IsWindows(), OperatingSystem.IsLinux()) switch
            {
                (true, _) => requestedPath,
                (_, true) => LinuxDescriptorPath(directoryHandle),
                _ => throw new PlatformNotSupportedException(HandleEnumerationUnavailableMessage)
            };

        return result;
    }

    private static DirectoryEntryIdentity GetIdentity(SafeFileHandle handle)
    {
        DirectoryEntryIdentity result = (OperatingSystem.IsWindows(),
                                         OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) switch
            {
                (true, _) => GetWindowsIdentity(handle),
                (_, true) => GetUnixIdentity(handle),
                _ => throw new PlatformNotSupportedException(StrongIdentityUnavailableMessage)
            };

        return result;
    }

    private static DirectoryEntryIdentity GetWindowsIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out WindowsFileInformation information))
            throw LastWindowsIOException(IdentityReadFailureMessage);
        ulong fileId = ((ulong)information.pmFileIndexHigh << BitsPerUInt32)
                       | information.pmFileIndexLow;
        return new DirectoryEntryIdentity(information.pmVolumeSerialNumber, 0, fileId);
    }

    private static DirectoryEntryIdentity GetUnixIdentity(SafeFileHandle handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(UnixStatBufferSize);
        DirectoryEntryIdentity result;
        try
        {
            if (FStat(handle, buffer) != 0)
                throw LastUnixIOException(IdentityReadFailureMessage);
            ulong volumeId = OperatingSystem.IsMacOS()
                ? unchecked((uint)Marshal.ReadInt32(buffer, UnixDeviceOffset))
                : unchecked((ulong)Marshal.ReadInt64(buffer, UnixDeviceOffset));
            ulong fileId = unchecked((ulong)Marshal.ReadInt64(buffer, UnixFileIdOffset));
            result = new DirectoryEntryIdentity(volumeId, 0, fileId);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static string ResolvePath(SafeFileHandle handle)
    {
        string result = (OperatingSystem.IsWindows(),
                         OperatingSystem.IsLinux(),
                         OperatingSystem.IsMacOS()) switch
            {
                (true, _, _) => ResolveWindowsPath(handle),
                (_, true, _) => ResolveLinuxPath(handle),
                (_, _, true) => ResolveMacPath(handle),
                _ => throw new PlatformNotSupportedException(ResolvedPathUnavailableMessage)
            };

        return Path.GetFullPath(result);
    }

    private static string ResolveWindowsPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(InitialWindowsPathCapacity);
        uint length = GetFinalPathNameByHandle(handle,
                                               buffer,
                                               (uint)buffer.Capacity,
                                               WindowsFinalPathFlags.NormalizedName);
        if (length == 0)
            throw LastWindowsIOException(PathResolutionFailureMessage);
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle,
                                              buffer,
                                              (uint)buffer.Capacity,
                                              WindowsFinalPathFlags.NormalizedName);
            if (length == 0 || length >= buffer.Capacity)
                throw LastWindowsIOException(PathResolutionFailureMessage);
        }

        return NormalizeWindowsDevicePath(buffer.ToString());
    }

    private static string ResolveLinuxPath(SafeFileHandle handle)
    {
        string descriptorPath = LinuxDescriptorPath(handle);
        string? target = new FileInfo(descriptorPath).LinkTarget;
        if (string.IsNullOrWhiteSpace(target))
            throw new IOException("The opened filesystem path could not be resolved.");
        return target;
    }

    private static string LinuxDescriptorPath(SafeFileHandle handle) =>
        $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";

    private static string ResolveMacPath(SafeFileHandle handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(MacPathBufferSize);
        string result;
        try
        {
            if (Fcntl(handle, MacGetPathCommand, buffer) == -1)
                throw LastUnixIOException(PathResolutionFailureMessage);
            string? path = Marshal.PtrToStringUTF8(buffer);
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException("The opened filesystem path could not be resolved.");
            result = path;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static string NormalizeWindowsDevicePath(string path)
    {
        string result = (path.StartsWith(WindowsUncPrefix, StringComparison.OrdinalIgnoreCase),
                         path.StartsWith(WindowsDevicePrefix, StringComparison.OrdinalIgnoreCase)) switch
            {
                (true, _) => $"\\\\{path[WindowsUncPrefix.Length..]}",
                (_, true) => path[WindowsDevicePrefix.Length..],
                _ => path
            };
        return result;
    }

    private static void ThrowIfInvalid(SafeFileHandle handle)
    {
        if (handle.IsInvalid)
        {
            IOException error = LastWindowsIOException(EntryOpenFailureMessage);
            handle.Dispose();
            throw error;
        }
    }

    private static IOException LastWindowsIOException(string message) =>
        new(message, Marshal.GetHRForLastWin32Error());

    private static IOException LastUnixIOException(string message)
    {
        int nativeError = Marshal.GetLastPInvokeError();
        return new IOException(message, unchecked((int)(WindowsHResultPrefix | (uint)nativeError)));
    }

    [DllImport(WindowsKernelLibrary,
               EntryPoint = "CreateFileW",
               CharSet = CharSet.Unicode,
               ExactSpelling = true,
               SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName,
                                                    WindowsFileAccess desiredAccess,
                                                    FileShare shareMode,
                                                    IntPtr securityAttributes,
                                                    FileMode creationDisposition,
                                                    WindowsFileFlags flagsAndAttributes,
                                                    IntPtr templateFile);

    [DllImport(WindowsKernelLibrary, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out WindowsFileInformation fileInformation);

    [DllImport(WindowsKernelLibrary,
               EntryPoint = "GetFinalPathNameByHandleW",
               CharSet = CharSet.Unicode,
               ExactSpelling = true,
               SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,
                                                        StringBuilder filePath,
                                                        uint filePathLength,
                                                        WindowsFinalPathFlags flags);

    [DllImport(UnixCLibrary, EntryPoint = "fstat", ExactSpelling = true, SetLastError = true)]
    private static extern int FStat(SafeFileHandle file, IntPtr buffer);

    [DllImport(UnixCLibrary, EntryPoint = "fcntl", ExactSpelling = true, SetLastError = true)]
    private static extern int Fcntl(SafeFileHandle file, int command, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileInformation
    {
        public FileAttributes pmFileAttributes;
        public WindowsFileTime pmCreationTime;
        public WindowsFileTime pmLastAccessTime;
        public WindowsFileTime pmLastWriteTime;
        public uint pmVolumeSerialNumber;
        public uint pmFileSizeHigh;
        public uint pmFileSizeLow;
        public uint pmNumberOfLinks;
        public uint pmFileIndexHigh;
        public uint pmFileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileTime
    {
        public uint pmLowDateTime;
        public uint pmHighDateTime;
    }

    [Flags]
    private enum WindowsFileAccess : uint
    {
        None = 0,
        ReadAttributes = 0x00000080
    }

    [Flags]
    private enum WindowsFileFlags : uint
    {
        None = 0,
        OpenReparsePoint = 0x00200000,
        BackupSemantics = 0x02000000
    }

    private enum WindowsFinalPathFlags : uint
    {
        NormalizedName = 0
    }

    private const string WindowsKernelLibrary = "kernel32.dll";
    private const string UnixCLibrary = "libc";
    private const string HandleEnumerationUnavailableMessage =
        "Handle-relative directory enumeration is unavailable on this operating system.";
    private const string StrongIdentityUnavailableMessage =
        "Strong filesystem entry identity is unavailable on this operating system.";
    private const string ResolvedPathUnavailableMessage =
        "Handle-resolved filesystem paths are unavailable on this operating system.";
    private const string IdentityReadFailureMessage = "Filesystem identity could not be read.";
    private const string PathResolutionFailureMessage =
        "The opened filesystem path could not be resolved.";
    private const string EntryOpenFailureMessage = "The filesystem entry could not be opened.";
    private const string WindowsDevicePrefix = "\\\\?\\";
    private const string WindowsUncPrefix = "\\\\?\\UNC\\";
    private const int BitsPerUInt32 = 32;
    private const int InitialWindowsPathCapacity = 512;
    private const int UnixStatBufferSize = 512;
    private const int UnixDeviceOffset = 0;
    private const int UnixFileIdOffset = 8;
    private const int MacGetPathCommand = 50;
    private const int MacPathBufferSize = 4096;
    private const uint WindowsHResultPrefix = 0x80070000;
}
