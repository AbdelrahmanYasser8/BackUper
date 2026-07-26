using System.Runtime.InteropServices;
using System.Text;

namespace BackUper.Services;

/// <summary>
/// Copies a locked file by enumerating system handles, duplicating the locking
/// process's file handle into our process, and reading via memory-mapped I/O.
/// Works on exclusive locks without admin and without closing the app.
/// Based on GhostPack/Lockless and HackBrowserData approaches.
/// </summary>
internal static class HandleDuplicateCopy
{
    #region P/Invoke declarations

    private const int SystemHandleInformationClass = 16;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    private const int ObjectNameInformationClass = 1;
    private const uint DUPLICATE_SAME_ACCESS = 0x00000002;
    private const uint FILE_MAP_READ = 0x0004;
    private const int FILE_TYPE_DISK = 0x0001;
    private const uint PROCESS_DUP_HANDLE = 0x0040;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryObject(
        IntPtr handle,
        int objectInformationClass,
        IntPtr objectInformation,
        int objectInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwOptions);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern int GetFileType(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetFinalPathNameByHandle(
        IntPtr hFile,
        [Out] StringBuilder lpszFilePath,
        int cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileSizeEx(IntPtr hFile, out long lpFileSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateFileMapping(
        IntPtr hFile,
        IntPtr lpFileMappingAttributes,
        uint flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        IntPtr lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(
        IntPtr hFileMappingObject,
        uint dwDesiredAccess,
        uint dwFileOffsetHigh,
        uint dwFileOffsetLow,
        IntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll")]
    private static extern bool ReadFile(
        IntPtr hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(
        IntPtr hFile, long liDistanceToMove, out long lpNewFilePointer, uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemHandleTableEntryInfo
    {
        public ushort OwnerProcessId;
        public ushort CreatorBackTraceIndex;
        public byte ObjectTypeNumber;
        public byte HandleAttributes;
        public short HandleValue;
        public IntPtr Object;
        public uint GrantedAccess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    #endregion

    private const long ChunkSize = 256L * 1024 * 1024; // 256 MB

    /// <summary>
    /// Attempts to copy a locked file by duplicating an existing handle to it.
    /// Returns true if successful.
    /// </summary>
    internal static bool TryCopy(string sourcePath, string destinationPath)
    {
        var normalizedSource = Path.GetFullPath(sourcePath).ToLowerInvariant();
        var targetSuffix = ExtractStableSuffix(normalizedSource);

        IntPtr handleBuffer = IntPtr.Zero;
        try
        {
            handleBuffer = GetAllHandles(out int handleCount);
            if (handleBuffer == IntPtr.Zero || handleCount == 0)
                return false;

            IntPtr currentProcess = GetCurrentProcess();
            int entrySize = Marshal.SizeOf<SystemHandleTableEntryInfo>();
            IntPtr handlesStart = handleBuffer + 8; // 4 bytes count + 4 padding

            for (int i = 0; i < handleCount; i++)
            {
                IntPtr entryPtr = handlesStart + (nint)((long)i * entrySize);
                var entry = Marshal.PtrToStructure<SystemHandleTableEntryInfo>(entryPtr);

                int pid = entry.OwnerProcessId;
                if (pid == 0 || pid == 4)
                    continue;

                if ((entry.GrantedAccess & PROCESS_DUP_HANDLE) == 0 &&
                    entry.GrantedAccess == 0)
                    continue;

                IntPtr sourceProcess = IntPtr.Zero;
                IntPtr dupHandle = IntPtr.Zero;
                try
                {
                    sourceProcess = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                    if (sourceProcess == IntPtr.Zero)
                        continue;

                    if (!DuplicateHandle(sourceProcess, entry.HandleValue, currentProcess,
                        out dupHandle, 0, false, DUPLICATE_SAME_ACCESS))
                        continue;

                    if (GetFileType(dupHandle) != FILE_TYPE_DISK)
                        continue;

                    string? filePath = GetFilePath(dupHandle);
                    if (string.IsNullOrEmpty(filePath))
                        continue;

                    string normFilePath = filePath.ToLowerInvariant();
                    if (normFilePath != normalizedSource && !normFilePath.EndsWith(targetSuffix))
                        continue;

                    byte[]? fileContent = ReadViaFileMapping(dupHandle);
                    if (fileContent == null)
                        fileContent = ReadViaReadFile(dupHandle);

                    if (fileContent != null)
                    {
                        File.WriteAllBytes(destinationPath, fileContent);
                        return true;
                    }

                    return false;
                }
                finally
                {
                    if (dupHandle != IntPtr.Zero) CloseHandle(dupHandle);
                    if (sourceProcess != IntPtr.Zero) CloseHandle(sourceProcess);
                }
            }
        }
        finally
        {
            if (handleBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(handleBuffer);
        }
        return false;
    }

    private static IntPtr GetAllHandles(out int handleCount)
    {
        handleCount = 0;
        int bufferSize = 0x100000; // 1MB initial
        IntPtr buffer = IntPtr.Zero;

        try
        {
            int returnLength;
            NtQuerySystemInformation(SystemHandleInformationClass, IntPtr.Zero, 0, out returnLength);
            bufferSize = Math.Max(returnLength, 0x100000);

            buffer = Marshal.AllocHGlobal(bufferSize);
            var status = NtQuerySystemInformation(SystemHandleInformationClass, buffer, bufferSize, out returnLength);

            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(buffer);
                bufferSize = returnLength + 0x10000;
                buffer = Marshal.AllocHGlobal(bufferSize);
                status = NtQuerySystemInformation(SystemHandleInformationClass, buffer, bufferSize, out returnLength);
            }

            if (status != 0)
            {
                Marshal.FreeHGlobal(buffer);
                return IntPtr.Zero;
            }

            handleCount = Marshal.ReadInt32(buffer);
            return buffer;
        }
        catch
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            return IntPtr.Zero;
        }
    }

    private static string? GetFilePath(IntPtr handle)
    {
        // Try GetFinalPathNameByHandle first — returns Win32 path directly
        var sb = new StringBuilder(260);
        int len = GetFinalPathNameByHandle(handle, sb, sb.Capacity, 0);
        if (len > 0 && len < sb.Capacity)
        {
            string path = sb.ToString();
            if (path.StartsWith(@"\\?\"))
                path = path[4..];
            return path;
        }

        // Fallback: NtQueryObject for NT device path
        int length = 0;
        NtQueryObject(handle, ObjectNameInformationClass, IntPtr.Zero, 0, out length);

        if (length == 0) return null;

        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            uint status = NtQueryObject(handle, ObjectNameInformationClass, ptr, length, out _);
            if (status != 0) return null;

            var name = Marshal.PtrToStructure<UnicodeString>(ptr);
            if (name.Length == 0 || name.Buffer == IntPtr.Zero) return null;

            string rawName = Marshal.PtrToStringUni(name.Buffer, name.Length / 2) ?? "";
            return ConvertNtPathToWin32(rawName);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static string ConvertNtPathToWin32(string ntPath)
    {
        if (!ntPath.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase))
            return ntPath;

        for (char c = 'A'; c <= 'Z'; c++)
        {
            string driveDevice = $"\\Device\\HarddiskVolume{c - 'A' + 1}";
            if (ntPath.StartsWith(driveDevice, StringComparison.OrdinalIgnoreCase))
            {
                string remainder = ntPath[driveDevice.Length..];
                return $"{c}:{remainder}";
            }
        }

        for (char c = 'A'; c <= 'Z'; c++)
        {
            var sb = new StringBuilder(260);
            if (QueryDosDevice($"{c}:", sb, (uint)sb.Capacity) != 0)
            {
                string deviceName = sb.ToString();
                if (ntPath.StartsWith(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = ntPath[deviceName.Length..];
                    return $"{c}:{remainder}";
                }
            }
        }

        return ntPath;
    }

    private static byte[]? ReadViaFileMapping(IntPtr handle)
    {
        if (!GetFileSizeEx(handle, out long fileSize) || fileSize == 0)
            return fileSize == 0 ? Array.Empty<byte>() : null;

        // For small files, use single mapping (faster)
        if (fileSize <= ChunkSize)
        {
            return ReadSingleMapping(handle, fileSize);
        }

        // For large files, use chunked mapping
        return ReadChunkedMapping(handle, fileSize);
    }

    private static byte[]? ReadSingleMapping(IntPtr handle, long fileSize)
    {
        IntPtr mapping = CreateFileMapping(handle, IntPtr.Zero, 0x02, 0, 0, IntPtr.Zero);
        if (mapping == IntPtr.Zero)
            return null;

        try
        {
            IntPtr view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, IntPtr.Zero);
            if (view == IntPtr.Zero)
                return null;

            try
            {
                byte[] data = new byte[fileSize];
                Marshal.Copy(view, data, 0, (int)fileSize);
                return data;
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
        finally
        {
            CloseHandle(mapping);
        }
    }

    private static byte[]? ReadChunkedMapping(IntPtr handle, long fileSize)
    {
        byte[] data = new byte[fileSize];
        long offset = 0;

        while (offset < fileSize)
        {
            long remaining = fileSize - offset;
            int chunkLen = (int)Math.Min(ChunkSize, remaining);

            uint offsetHigh = (uint)(offset >> 32);
            uint offsetLow = (uint)(offset & 0xFFFFFFFF);

            IntPtr mapping = CreateFileMapping(handle, IntPtr.Zero, 0x02, 0, 0, IntPtr.Zero);
            if (mapping == IntPtr.Zero)
                return null;

            try
            {
                IntPtr view = MapViewOfFile(mapping, FILE_MAP_READ, offsetHigh, offsetLow, (IntPtr)chunkLen);
                if (view == IntPtr.Zero)
                    return null;

                try
                {
                    Marshal.Copy(view, data, (int)offset, chunkLen);
                }
                finally
                {
                    UnmapViewOfFile(view);
                }
            }
            finally
            {
                CloseHandle(mapping);
            }

            offset += chunkLen;
        }

        return data;
    }

    private static byte[]? ReadViaReadFile(IntPtr handle)
    {
        if (!GetFileSizeEx(handle, out long fileSize) || fileSize == 0)
            return fileSize == 0 ? Array.Empty<byte>() : null;

        SetFilePointerEx(handle, 0, out _, 0); // FILE_BEGIN

        byte[] data = new byte[fileSize];
        int totalRead = 0;

        while (totalRead < fileSize)
        {
            int toRead = (int)Math.Min(fileSize - totalRead, int.MaxValue);
            if (!ReadFile(handle, data, (uint)toRead, out uint bytesRead, IntPtr.Zero) || bytesRead == 0)
                return null;

            totalRead += (int)bytesRead;
        }

        return data;
    }

    private static string ExtractStableSuffix(string fullPath)
    {
        var parts = fullPath.Split('\\');
        if (parts.Length <= 3) return fullPath.ToLowerInvariant();

        var distinctive = new[] { "onedrive", "appdata", "local", "temp", "desktop", "documents", "downloads", "windows", "program files" };
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (distinctive.Contains(parts[i].ToLowerInvariant()))
            {
                return string.Join("\\", parts.Skip(i)).ToLowerInvariant();
            }
        }

        int start = Math.Max(0, parts.Length - 4);
        return string.Join("\\", parts.Skip(start)).ToLowerInvariant();
    }
}