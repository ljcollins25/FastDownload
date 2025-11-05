// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.ComponentModel;
using System.Diagnostics.ContractsLight;
using System.Runtime.InteropServices;
using FastDownload.Shared;
using Microsoft.Win32.SafeHandles;

#nullable enable

namespace FastDownload.Utilities;

internal static class WindowsNativeMethods
{
    /// <summary>
    /// Gets all data regions in a sparse file
    /// </summary>
    /// <param name="fileStream">The file stream to analyze</param>
    /// <returns>List of data regions (non-hole parts) in the file</returns>
    public static List<Chunk> GetDataRegions(FileStream fileStream)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Sparse region enumeration using this method is only supported on Windows.");
        }

        var regions = new List<Chunk>();
        long fileSize = fileStream.Length;
        long currentOffset = 0;

        FILE_ALLOCATED_RANGE_BUFFER queryRange = new FILE_ALLOCATED_RANGE_BUFFER
        {
            FileOffset = 0,
            Length = fileSize
        };

        var itemSize = Marshal.SizeOf(queryRange);

        // Use a stack-allocated buffer for performance and safety
        Span<FILE_ALLOCATED_RANGE_BUFFER> outBuffer = stackalloc FILE_ALLOCATED_RANGE_BUFFER[64];

        while (currentOffset < fileSize)
        {
            queryRange.FileOffset = currentOffset;
            queryRange.Length = fileSize - currentOffset;

            int bytesReturned = 0;
            bool result = DeviceIoControl_AllocatedRanges(
                fileStream.SafeFileHandle,
                FSCTL_QUERY_ALLOCATED_RANGES,
                ref queryRange,
                Marshal.SizeOf(queryRange),
                ref MemoryMarshal.GetReference(outBuffer),
                itemSize * outBuffer.Length,
                ref bytesReturned,
                IntPtr.Zero);

            if (!result)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 234) // ERROR_HAS_MORE_DATA
                {
                    // Has more data. Need to continue.
                }
                else if (error == 38) // ERROR_HANDLE_EOF
                {
                    break;
                }
                else
                {
                    throw new Win32Exception(error);
                }
            }

            int count = bytesReturned / itemSize;
            if (count == 0)
            {
                break;
            }

            for (int i = 0; i < count; i++)
            {
                var range = outBuffer[i];
                regions.Add(Chunk.FromStartAndLength(range.FileOffset, range.Length));
                currentOffset = range.FileOffset + range.Length;
            }
        }

        return regions;
    }

    private const uint FSCTL_QUERY_ALLOCATED_RANGES = 0x940CF;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ALLOCATED_RANGE_BUFFER
    {
        public long FileOffset;
        public long Length;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto, EntryPoint = nameof(DeviceIoControl))]
    private static extern unsafe bool DeviceIoControl_AllocatedRanges(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref FILE_ALLOCATED_RANGE_BUFFER InBuffer,
        int nInBufferSize,
        ref FILE_ALLOCATED_RANGE_BUFFER OutBuffer,
        int nOutBufferSize,
        ref int pBytesReturned,
        IntPtr lpOverlapped);

    public static bool SetSparseFlag(this FileStream fileStream, bool value)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        // Initialize FILE_SET_SPARSE_BUFFER structure
        var sparseBuffer = new FILE_SET_SPARSE_BUFFER
        {
            SetSparse = value
        };

        int bytesReturned = 0;
        var lpOverlapped = default(NativeOverlapped);
        return DeviceIoControl(
                fileStream.SafeFileHandle,
                FSCTL_SET_SPARSE,
                ref sparseBuffer,
                Marshal.SizeOf(sparseBuffer),
                nint.Zero,
                0,
                ref bytesReturned,
                ref lpOverlapped);
    }

    public static bool IsSparse(this FileStream fileStream)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        BY_HANDLE_FILE_INFORMATION fileInfo;
        if (!GetFileInformationByHandle(fileStream.SafeFileHandle.DangerousGetHandle(), out fileInfo))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return (fileInfo.FileAttributes & FILE_ATTRIBUTE_SPARSE_FILE) == FILE_ATTRIBUTE_SPARSE_FILE;
    }

    /// <summary>
    /// Unbuffered IO requires that the buffer be aligned on a sector boundary. Gather/Scatter IO requires that the
    /// buffer be aligned on a memory page boundary. This method computes the alignment that satisfies both
    /// requirements.
    /// </summary>
    public static uint ComputeUnbufferedIOAlignment(FileStream fileStream)
    {
        var memoryAlignment = Environment.SystemPageSize;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return (uint)memoryAlignment;
        }

        // See: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefilegather
        var fileStorageInfo = GetFileStorageInfo(fileStream.SafeFileHandle);
        Contract.Assert(fileStorageInfo != null);
        // See: https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering#alignment-and-file-access-requirements
        var diskAlignment = fileStorageInfo!.Value.PhysicalBytesPerSectorForPerformance;
        var alignment = (uint)Maths.LowestCommonMultiple((int)diskAlignment, memoryAlignment);
        return alignment;
    }

    #region DeviceIoControl FFI

    [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref FILE_SET_SPARSE_BUFFER lpInBuffer,
        int nInBufferSize,
        nint OutBuffer,
        int nOutBufferSize,
        ref int pBytesReturned,
        [In] ref NativeOverlapped lpOverlapped);

    // Define the FSCTL_SET_SPARSE constant
    private const uint FSCTL_SET_SPARSE = 0x900C4;

    // Define the FILE_SET_SPARSE_BUFFER structure
    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_SET_SPARSE_BUFFER
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool SetSparse;
    }

    #endregion

    #region GetFileInformationByHandle FFI

    [StructLayout(LayoutKind.Sequential)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(IntPtr hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    private const uint FILE_ATTRIBUTE_SPARSE_FILE = 0x00000200;

    #endregion

    #region GetFileInformationByHandleEx FFI

    public static FILE_STORAGE_INFO? GetFileStorageInfo(SafeFileHandle hFile)
    {
        FILE_STORAGE_INFO fileStorageInfo = default;
        unsafe
        {
            if (!GetFileInformationByHandleEx(hFile, FILE_INFO_BY_HANDLE_CLASS.FileStorageInfo, (nint)(&fileStorageInfo), Marshal.SizeOf<FILE_STORAGE_INFO>()))
            {
                return null;
            }

            return fileStorageInfo;
        }
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetFileInformationByHandleEx(
            SafeFileHandle hFile,
            FILE_INFO_BY_HANDLE_CLASS FileInformationClass,
            nint lpFileInformation,
            int dwBufferSize);

    public enum FILE_INFO_BY_HANDLE_CLASS : uint
    {
        FileStorageInfo = 16
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILE_STORAGE_INFO
    {
        public uint LogicalBytesPerSector;
        public uint PhysicalBytesPerSectorForAtomicity;
        public uint PhysicalBytesPerSectorForPerformance;
        public uint FileSystemEffectivePhysicalBytesPerSectorForAtomicity;
        public uint Flags;
        public uint ByteOffsetForSectorAlignment;
        public uint ByteOffsetForPartitionAlignment;
    }

    #endregion
}
