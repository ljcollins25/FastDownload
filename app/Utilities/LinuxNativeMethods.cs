// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Runtime.InteropServices;
using FastDownload.Shared;

#nullable enable

namespace FastDownload.Utilities;

internal static class LinuxNativeMethods
{
    private const int SEEK_DATA = 3;
    private const int SEEK_HOLE = 4;

    [DllImport("libc", SetLastError = true)]
    private static extern long lseek(int fd, long offset, int whence);

    /// <summary>
    /// Gets all data regions in a sparse file
    /// </summary>
    /// <param name="fileStream">The file stream to analyze</param>
    /// <returns>List of data regions (non-hole parts) in the file</returns>
    public static List<Chunk> GetDataRegions(FileStream fileStream)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // On non-Linux, treat the entire file as one data region
            return new List<Chunk> { new Chunk(0, fileStream.Length) };
        }

        int fd = fileStream.SafeFileHandle.DangerousGetHandle().ToInt32();
        var regions = new List<Chunk>();
        long fileSize = fileStream.Length;

        if (fileSize == 0)
        {
            return regions; // Empty file has no data regions
        }

        // Start from the beginning of the file
        long offset = 0;

        while (offset < fileSize)
        {
            // Find the next data region
            long dataStart = lseek(fd, offset, SEEK_DATA);
            if (dataStart == -1 || dataStart >= fileSize)
            {
                // No more data regions
                break;
            }

            // Find the next hole
            long dataEnd = lseek(fd, dataStart, SEEK_HOLE);
            if (dataEnd == -1)
            {
                // Error or no more holes - treat the rest as data
                dataEnd = fileSize;
            }

            regions.Add(new Chunk(dataStart, dataEnd));
            offset = dataEnd;
        }

        return regions;
    }

    /// <summary>
    /// Gets the physical disk sector size for a file on Linux systems.
    /// </summary>
    /// <param name="fileStream">The file stream to get the sector size for</param>
    /// <returns>The sector size in bytes</returns>
    public static uint GetLinuxDiskSectorSize(FileStream fileStream)
    {
        // Default value if we can't determine the actual size (4KB is common for modern disks)
        var memoryAlignment = Environment.SystemPageSize;
        uint defaultSectorSize = (uint)Chunk.Align(4096, memoryAlignment, roundUp: true);

        try
        {
            if (fileStream.SafeFileHandle == null || fileStream.SafeFileHandle.IsInvalid)
            {
                return defaultSectorSize;
            }

            // Get file descriptor
            int fd = fileStream.SafeFileHandle.DangerousGetHandle().ToInt32();

            // Try to get the physical block size using ioctl
            uint physicalSectorSize = 0;

            // Try BLKPBSZGET first (physical sector size)
            if (Ioctl(fd, BLKPBSZGET, ref physicalSectorSize) >= 0 && physicalSectorSize > 0)
            {
                return physicalSectorSize;
            }

            // Fall back to BLKSSZGET (logical sector size)
            if (Ioctl(fd, BLKSSZGET, ref physicalSectorSize) >= 0 && physicalSectorSize > 0)
            {
                return physicalSectorSize;
            }

            // Try statfs as a final fallback
            var statFs = default(StatfsBuffer);
            if (Statfs(fileStream.Name, ref statFs) == 0 && statFs.f_bsize > 0)
            {
                return (uint)statFs.f_bsize;
            }

            return defaultSectorSize;
        }
        catch
        {
            return defaultSectorSize;
        }
    }

    // Linux-specific P/Invoke declarations
    private const ulong BLKPBSZGET = 0x127B; // Get physical block size
    private const ulong BLKSSZGET = 0x1268;  // Get logical block size

    [DllImport("libc", SetLastError = true)]
    private static extern int Ioctl(int fd, ulong request, ref uint arg);

    [StructLayout(LayoutKind.Sequential)]
    private struct StatfsBuffer
    {
        public long f_type;
        public long f_bsize;    // Filesystem block size
        public long f_blocks;
        public long f_bfree;
        public long f_bavail;
        public long f_files;
        public long f_ffree;
        public long f_fsid;
        public long f_namelen;
        public long f_frsize;
        public long f_flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public long[] f_spare;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int Statfs([MarshalAs(UnmanagedType.LPStr)] string path, ref StatfsBuffer buf);
}
