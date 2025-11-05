// Copyright (C) Microsoft Corporation. All Rights Reserved.

#nullable enable

using System.Diagnostics.ContractsLight;
using System.Runtime.InteropServices;
using FastDownload.Shared;

namespace FastDownload.Utilities;

internal static class FileUtilities
{
    public static FileStream OpenUnbufferedFileStream(string path, FileMode fileMode, bool writeThrough, bool async)
    {
        FileOptions flags = 0;
        if (writeThrough)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // NoBuffering is a hint to the OS that we don't want to buffer IO. This is important for files that won't be
                // read from and will be written to sequentially in sufficiently large chunks. This is the case for our
                // download scenario.
                //
                // Note that this flag is undocumented. This is on purpose because unbuffered IO has very specific requirements
                // that will fail the program if not met. Nevertheless, the flag is actually supported.
                flags |= (FileOptions)0x20000000;
            }

            flags |= FileOptions.WriteThrough;
        }

        if (async)
        {
            flags |= FileOptions.Asynchronous;
        }

        return new FileStream(
                    path,
                    fileMode,
                    FileAccess.ReadWrite,
                    // Ensure we can subsequently re-open the file and keep multiple descriptors open. This is
                    // important because we do writes in parallel using multiple descriptors.
                    FileShare.ReadWrite,
                    0,
                    flags);
    }

    public static uint GetChunkLength(long chunkStart, long fileSize, uint chunkSize)
    {
        Contract.Assert(chunkStart <= fileSize);
        Contract.Assert((chunkStart % chunkSize) == 0);

        return (uint)Math.Min(chunkSize, fileSize - chunkStart);
    }

    public static uint ComputeUnbufferedIOAlignment(this FileStream fileStream)
    {
        var memoryAlignment = Environment.SystemPageSize;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Existing Windows implementation
            return WindowsNativeMethods.ComputeUnbufferedIOAlignment(fileStream);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // New Linux implementation
            uint diskSectorSize = LinuxNativeMethods.GetLinuxDiskSectorSize(fileStream);
            return (uint)Maths.LowestCommonMultiple((int)diskSectorSize, memoryAlignment);
        }

        // Default fallback for other platforms
        return (uint)memoryAlignment;
    }

    /// <summary>
    /// Retrieves the data regions of a file stream.
    /// This method is platform-specific and will return the data regions for Windows or Linux.
    /// If no data regions are found, it returns a single region covering the entire file length.
    /// </summary>
    public static List<Chunk> GetDataRegions(this FileStream fileStream)
    {
        var result = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsNativeMethods.GetDataRegions(fileStream)
            : LinuxNativeMethods.GetDataRegions(fileStream);

        if (result.Count == 0)
        {
            // Never return an empty list
            return [new Chunk(0, fileStream.Length)];
        }

        return result;
    }
}
