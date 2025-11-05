// Copyright (C) Microsoft Corporation. All Rights Reserved.

using Microsoft.Net.Http.Headers;

namespace FastDownload.Shared;

#nullable enable

/// <summary>
/// Represents a 0-indexed segment of data.
///
/// For example:
///  - (0, 1) => Represents a 1-byte segment starting at byte 0
///  - (0, 0) => Represents an empty segment starting at byte 0
/// </summary>
/// <param name="Start">Start of the segment, inclusive</param>
/// <param name="End">End of the segment, exclusive</param>
internal record struct Chunk(long Start, long End) : IComparable<Chunk>
{
    public long Length => End - Start;

    public override string ToString()
    {
        return $"[{Start}, {End})(Length = {Length})";
    }

    public static Chunk FromStartAndLength(long start, long length)
    {
        return new Chunk(start, start + length);
    }

    public Chunk Shift(long shift)
    {
        return FromStartAndLength(Start + shift, Length);
    }

    public Chunk? Intersect(Chunk other)
    {
        var start = Math.Max(Start, other.Start);
        var end = Math.Min(End, other.End);
        return start <= end ? new(start, end) : null;
    }

    public static Chunk FromRange(RangeHeaderValue range)
    {
        var firstRange = range.Ranges.Single();
        return new Chunk(firstRange.From!.Value, firstRange.To!.Value + 1);
    }

    public int CompareTo(Chunk other)
    {
        return Start.CompareTo(other.Start);
    }

    /// <summary>
    /// Aligns the chunk to the specified alignment.
    /// The start is aligned down to equal or lesser value, and the end is aligned up to equal or greater aligned.
    /// </summary>
    public Chunk AlignTo(long alignment)
    {
        return new Chunk(Align(Start, alignment, roundUp: false), Align(End, alignment, roundUp: true));
    }

    /// <summary>
    /// Returns a new chunk that is the union of this chunk and another chunk.
    /// The start is the minimum of both starts, and the end is the maximum of both
    /// </summary>
    public Chunk Union(Chunk other)
    {
        return new Chunk(Math.Min(Start, other.Start), Math.Max(End, other.End));
    }

    /// <summary>
    /// Returns the number of chunks that would be created a chunk from [0, <paramref name="totalSize"/>) chunk were split into chunks of the specified size/alignment.
    /// </summary>
    public static int Count(long totalSize, long chunkSize)
    {
        return (int)(Align(totalSize, chunkSize, roundUp: true) / chunkSize);
    }

    public static long Align(long value, long alignment, bool roundUp = false)
    {
        if (roundUp)
        {
            value += (alignment - 1);
        }

        return (value / alignment) * alignment;
    }

    public void Validate(long fileSize)
    {
        if (Start < 0)
        {
            throw new ArgumentException("Chunk start must be non-negative");
        }

        if (End < 0)
        {
            throw new ArgumentException("Chunk end must be non-negative");
        }

        if (End < Start)
        {
            throw new ArgumentException("Chunk end must be greater than or equal to start");
        }

        if (End > fileSize)
        {
            throw new ArgumentException("Chunk end must be less than the file size");
        }
    }

    public static IEnumerable<Chunk> Split(long fileSize, long chunks, uint chunkSize, uint alignment)
    {
        if (fileSize < 0)
        {
            throw new ArgumentException("File size must be non-negative", nameof(fileSize));
        }

        if (chunks < 0)
        {
            throw new ArgumentException("Number of chunks must be greater than 0", nameof(chunks));
        }

        if (chunkSize <= 0)
        {
            throw new ArgumentException("Chunk size must be positive", nameof(chunkSize));
        }

        if (alignment <= 0)
        {
            throw new ArgumentException("Alignment must be positive", nameof(alignment));
        }

        if (chunkSize % alignment != 0)
        {
            throw new ArgumentException("Chunk size must be aligned to sector sizes", nameof(chunkSize));
        }

        for (var i = 0; i < chunks; i++)
        {
            var chunk = new Chunk(Start: i * chunkSize, End: Math.Min((i + 1) * chunkSize, fileSize));

            if (chunk.Start % alignment != 0)
            {
                throw new InvalidOperationException("Chunk starts must be aligned to sector sizes");
            }

            if (i < chunks - 1 && chunk.Length % alignment != 0)
            {
                throw new InvalidOperationException("Chunk sizes must be aligned to sector sizes");
            }

            if (i < chunks - 1 && chunk.End >= fileSize)
            {
                throw new ArgumentException("Miscalculated number of chunks detected.", nameof(chunkSize));
            }

            yield return chunk;
        }
    }
}
