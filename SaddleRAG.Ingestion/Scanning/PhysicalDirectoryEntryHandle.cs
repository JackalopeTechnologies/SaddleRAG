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
        SafeFileHandle result = (OperatingSystem.IsWindows(),
                                 OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) switch
            {
                (true, _) => OpenWindowsMetadata(fullPath),
                (_, true) => OpenUnixMetadata(fullPath),
                _ => File.OpenHandle(fullPath,
                                     FileMode.Open,
                                     FileAccess.Read,
                                     FileShare.Read | FileShare.Write)
            };

        return result;
    }

    private static SafeFileHandle OpenWindowsMetadata(string fullPath)
    {
        SafeFileHandle result = CreateFile(fullPath,
                                           WindowsFileAccess.ReadAttributes,
                                           FileShare.Read | FileShare.Write,
                                           IntPtr.Zero,
                                           FileMode.Open,
                                           WindowsFileFlags.BackupSemantics
                                           | WindowsFileFlags.OpenReparsePoint,
                                           IntPtr.Zero);
        ThrowIfInvalid(result);
        return result;
    }

    private static SafeFileHandle OpenUnixMetadata(string fullPath)
    {
        int flags = OperatingSystem.IsLinux()
            ? LinuxCloseOnExecOpenFlag
            : MacCloseOnExecOpenFlag;
        int descriptor = OpenUnixDescriptor(fullPath, flags);
        if (descriptor == 0)
            descriptor = DuplicateZeroDescriptor(descriptor);

        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

    private static int OpenUnixDescriptor(string fullPath, int flags)
    {
        int descriptor;
        int nativeError;
        do
        {
            descriptor = Open(fullPath, flags);
            nativeError = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
        } while(descriptor < 0 && nativeError == UnixInterruptedError);

        if (descriptor < 0)
            throw UnixOpenException(nativeError);
        return descriptor;
    }

    private static int DuplicateZeroDescriptor(int descriptor)
    {
        int command = OperatingSystem.IsLinux()
            ? LinuxDuplicateCloseOnExecCommand
            : MacDuplicateCloseOnExecCommand;
        int duplicate;
        int nativeError;
        do
        {
            duplicate = Fcntl(descriptor, command, MinimumOwnedUnixDescriptor);
            nativeError = duplicate < 0 ? Marshal.GetLastPInvokeError() : 0;
        } while(duplicate < 0 && nativeError == UnixInterruptedError);

        int closeResult = Close(descriptor);
        if (duplicate < 0)
            throw UnixOpenException(nativeError);
        if (closeResult != 0)
        {
            int closeError = Marshal.GetLastPInvokeError();
            new SafeFileHandle(new IntPtr(duplicate), ownsHandle: true).Dispose();
            throw UnixIOException(EntryCloseFailureMessage, closeError);
        }

        return duplicate;
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
        if (PathsResolveDifferently(canonicalPath, resolvedPath))
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

    private static bool PathsResolveDifferently(string canonicalPath, string resolvedPath)
    {
        bool result = !PathsEqual(canonicalPath, resolvedPath);
        if (result && OperatingSystem.IsWindows())
            result = !WindowsPathsAreAliases(canonicalPath, resolvedPath);
        return result;
    }

    private static bool WindowsPathsAreAliases(string requestedPath, string resolvedPath)
    {
        bool requestedConverted = TryGetWindowsShortPath(requestedPath, out string requestedShortPath);
        bool resolvedConverted = TryGetWindowsShortPath(resolvedPath, out string resolvedShortPath);
        return requestedConverted
               && resolvedConverted
               && PathsEqual(NormalizeWindowsDevicePath(requestedShortPath),
                             NormalizeWindowsDevicePath(resolvedShortPath));
    }

    private static bool TryGetWindowsShortPath(string fullPath, out string shortPath)
    {
        var buffer = new StringBuilder(InitialWindowsPathCapacity);
        uint length = GetShortPathName(fullPath, buffer, (uint)buffer.Capacity);
        bool succeeded = length > 0;
        if (succeeded && length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetShortPathName(fullPath, buffer, (uint)buffer.Capacity);
            succeeded = length > 0 && length < buffer.Capacity;
        }

        shortPath = succeeded ? buffer.ToString() : string.Empty;
        return succeeded;
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
        return UnixIOException(message, nativeError);
    }

    private static Exception UnixOpenException(int nativeError)
    {
        IOException error = UnixIOException(EntryOpenFailureMessage, nativeError);
        return nativeError is UnixOperationNotPermittedError or UnixAccessDeniedError
            ? new UnauthorizedAccessException(EntryOpenFailureMessage, error)
            : error;
    }

    private static IOException UnixIOException(string message, int nativeError) =>
        new(message, unchecked((int)(WindowsHResultPrefix | (uint)nativeError)));

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

    [DllImport(WindowsKernelLibrary,
               EntryPoint = "GetShortPathNameW",
               CharSet = CharSet.Unicode,
               ExactSpelling = true,
               SetLastError = true)]
    private static extern uint GetShortPathName(string longPath,
                                                StringBuilder shortPath,
                                                uint shortPathLength);

    [DllImport(UnixCLibrary, EntryPoint = "fstat", ExactSpelling = true, SetLastError = true)]
    private static extern int FStat(SafeFileHandle file, IntPtr buffer);

    [DllImport(UnixCLibrary, EntryPoint = "open", ExactSpelling = true, SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport(UnixCLibrary, EntryPoint = "close", ExactSpelling = true, SetLastError = true)]
    private static extern int Close(int file);

    [DllImport(UnixCLibrary, EntryPoint = "fcntl", ExactSpelling = true, SetLastError = true)]
    private static extern int Fcntl(SafeFileHandle file, int command, IntPtr buffer);

    [DllImport(UnixCLibrary, EntryPoint = "fcntl", ExactSpelling = true, SetLastError = true)]
    private static extern int Fcntl(int file, int command, int value);

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
    private const string EntryCloseFailureMessage = "The filesystem entry handle could not be closed.";
    private const string WindowsDevicePrefix = "\\\\?\\";
    private const string WindowsUncPrefix = "\\\\?\\UNC\\";
    private const int BitsPerUInt32 = 32;
    private const int InitialWindowsPathCapacity = 512;
    private const int UnixStatBufferSize = 512;
    private const int UnixDeviceOffset = 0;
    private const int UnixFileIdOffset = 8;
    private const int UnixOperationNotPermittedError = 1;
    private const int UnixInterruptedError = 4;
    private const int UnixAccessDeniedError = 13;
    private const int LinuxCloseOnExecOpenFlag = 0x00080000;
    private const int MacCloseOnExecOpenFlag = 0x01000000;
    private const int LinuxDuplicateCloseOnExecCommand = 1030;
    private const int MacDuplicateCloseOnExecCommand = 67;
    private const int MinimumOwnedUnixDescriptor = 3;
    private const int MacGetPathCommand = 50;
    private const int MacPathBufferSize = 4096;
    private const uint WindowsHResultPrefix = 0x80070000;
}
