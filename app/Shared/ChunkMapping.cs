// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Shared;
#nullable enable

/// <summary>
/// Information about a file chunk mapping (optionally to sparse regions)
/// </summary>
/// <param name="Chunk">the region in the file</param>
/// <param name="SparseRegions">the sparse regions which should be read/written for the file from this chunk</param>
internal record struct ChunkMapping(Chunk Chunk, Chunk[]? SparseRegions = null)
{
    public long UsedCapacity => SparseRegions?.Sum(c => c.Length) ?? Chunk.Length;
}

