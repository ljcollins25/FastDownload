// fdidx: turn a FastDownload manifest into a CfLazy chunk index (CFIX), so CfLazy can serve a blob that FastDownload itself uploaded.
//
// Requirements on the FastDownload upload (otherwise this refuses with a message):
//   --block-size 1048576      each FastDownload block is then one independent Brotli stream of 1 MiB = one CfLazy chunk
//   --sparse-handling SkipHoles   (not Compact: Compact packs several regions into one block, which breaks the 1:1 chunk mapping)
//   --compression Brotli      (the default; blocks FastDownload stores uncompressed, comp None, are indexed as raw chunks)
// Blocks FastDownload skipped (all-hole blocks) become zero chunks (length 0) in the index.
// FastDownload writes the compressed blocks back to back in offset order, so index entry i is just (compressed offset of block i).
using System.Text.Json;

static class FdIndex
{
    public static int Run(string manifestPath, string outIdx)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath)); var m = doc.RootElement;
        long size = m.GetProperty("RawSize").GetInt64(), bs = m.GetProperty("RawBlockSize").GetInt64();
        const int CS = 1 << 20;
        if (bs != CS) throw new Exception($"manifest RawBlockSize is {bs}; CfLazy serves 1 MiB chunks (CS in Serve.cs) - upload with --block-size {CS}");
        long n = (size + CS - 1) / CS; var e = new ulong[n + 1]; var seen = new bool[n]; long pos = 0, present = 0;
        var blocks = m.GetProperty("Blocks").EnumerateArray().Select(b => (
            comp: b.GetProperty("Compression").GetString(), ro: b.GetProperty("RawSlice").GetProperty("Offset").GetInt64(), rl: b.GetProperty("RawSlice").GetProperty("Length").GetInt64(),
            co: b.GetProperty("CompressedSlice").GetProperty("Offset").GetInt64(), cl: b.GetProperty("CompressedSlice").GetProperty("Length").GetInt64(),
            sparse: b.TryGetProperty("SparseRegions", out var sr) && sr.ValueKind == JsonValueKind.Array && sr.GetArrayLength() > 0 && !(sr.GetArrayLength() == 1 && sr[0].GetProperty("Offset").GetInt64() == b.GetProperty("RawSlice").GetProperty("Offset").GetInt64() && sr[0].GetProperty("Length").GetInt64() == b.GetProperty("RawSlice").GetProperty("Length").GetInt64()))).OrderBy(b => b.ro).ToList();
        foreach (var b in blocks)
        {
            if (b.comp != "Brotli" && b.comp != "None") throw new Exception("block compression " + b.comp + " not supported (use Brotli; None = stored raw)");
            if (b.sparse) throw new Exception("block has partial sparse regions (Compact mode); upload with --sparse-handling SkipHoles");
            if (b.ro % CS != 0 || b.rl != Math.Min(CS, size - b.ro)) throw new Exception($"block at {b.ro} has raw length {b.rl}, expected one whole 1 MiB chunk");
            if (b.co != pos) throw new Exception($"compressed blocks are not contiguous in offset order (block at {b.ro}: {b.co} != {pos})");
            long i = b.ro / CS; if (seen[i]) throw new Exception("duplicate block " + i); seen[i] = true; present++;
            e[i] = (ulong)b.co << 1; pos += b.cl; if (b.comp == "None") e[i] |= 1UL;   // bit 0 = chunk stored raw (FastDownload keeps incompressible blocks uncompressed)
        }
        // absent (skipped) chunks: same offset as the next present chunk => length 0 => zero chunk. Fill from the back.
        ulong next = (ulong)pos << 1; e[n] = next;
        for (long i = n - 1; i >= 0; i--) { if (seen[i]) next = e[i] & ~1UL; else e[i] = next; }
        new ChunkIndex { ChunkSize = CS, ImageSize = size, E = e }.Save(outIdx);
        Console.WriteLine($"fdidx: {n} chunks, {present} stored, {n - present} zero; compressed {pos >> 20} MiB of {size >> 20} MiB image");
        return 0;
    }
}
