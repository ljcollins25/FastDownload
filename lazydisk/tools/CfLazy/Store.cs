// Chunk store: image split in independently compressed chunks (default 1 MiB, brotli q1) + offset index.
// index file: "CFIX" | int chunkSize | long imageSize | int n | ulong[n+1]  (entry = offset<<1 | rawFlag; length = next.offset - offset; length 0 = all-zero chunk)
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;

sealed class ChunkIndex
{
    public int ChunkSize; public long ImageSize; public ulong[] E = Array.Empty<ulong>();
    public int N => E.Length - 1;
    public long ChunkLen(long i) => Math.Min(ChunkSize, ImageSize - i * ChunkSize);
    public (long off, int len, bool raw) Entry(long i) { long o = (long)(E[i] >> 1); return (o, (int)((long)(E[i + 1] >> 1) - o), (E[i] & 1) != 0); }
    public void Save(string path)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(new[] { (byte)'C', (byte)'F', (byte)'I', (byte)'X' }); w.Write(ChunkSize); w.Write(ImageSize); w.Write(N);
        foreach (var e in E) w.Write(e);
    }
    public static ChunkIndex Load(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (new string(r.ReadChars(4)) != "CFIX") throw new Exception("bad index");
        var x = new ChunkIndex { ChunkSize = r.ReadInt32(), ImageSize = r.ReadInt64() }; int n = r.ReadInt32();
        x.E = new ulong[n + 1]; for (int i = 0; i <= n; i++) x.E[i] = r.ReadUInt64(); return x;
    }
}

interface IRange { byte[] Read(long off, int len); }

sealed class FileRange : IRange
{
    readonly Microsoft.Win32.SafeHandles.SafeFileHandle h; readonly int latencyMs;
    public static long Gets, Wire;
    public FileRange(string p, int latencyMs) { h = File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess); this.latencyMs = latencyMs; }
    public byte[] Read(long off, int len)
    {
        var b = new byte[len]; int got = 0; while (got < len) { int r = RandomAccess.Read(h, b.AsSpan(got), off + got); if (r <= 0) throw new EndOfStreamException(); got += r; }
        if (latencyMs > 0) Thread.Sleep(latencyMs);
        Interlocked.Increment(ref Gets); Interlocked.Add(ref Wire, len); return b;
    }
}

sealed class HttpRange : IRange
{
    static readonly HttpClient http = new(new SocketsHttpHandler { MaxConnectionsPerServer = 64, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(2) };
    readonly Func<string> refresh; string url; readonly object lk = new();
    public static long Gets, Wire;
    public HttpRange(Func<string> refresh) { this.refresh = refresh; url = refresh(); }
    public byte[] Read(long off, int len)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                string u; lock (lk) u = url;
                using var rq = new HttpRequestMessage(HttpMethod.Get, u); rq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(off, off + len - 1);
                using var rs = http.Send(rq, HttpCompletionOption.ResponseHeadersRead);
                if (rs.StatusCode == HttpStatusCode.Forbidden || rs.StatusCode == HttpStatusCode.Unauthorized) { lock (lk) { if (url == u) url = refresh(); } continue; }
                if (rs.StatusCode != HttpStatusCode.PartialContent) throw new Exception("HTTP " + (int)rs.StatusCode);
                var b = new byte[len]; using var s = rs.Content.ReadAsStream(); int got = 0;
                while (got < len) { int r = s.Read(b, got, len - got); if (r <= 0) throw new EndOfStreamException(); got += r; }
                Interlocked.Increment(ref Gets); Interlocked.Add(ref Wire, len); return b;
            }
            catch (Exception) when (attempt < 4) { Thread.Sleep(200 * (attempt + 1)); }
        }
    }
}

// Decompressed access to the image by chunk.
sealed class ChunkSource
{
    public ChunkIndex? Idx; public IRange? Data; public string? RawPath; public long ImageSize; public int ChunkSize = 1 << 20;
    Microsoft.Win32.SafeHandles.SafeFileHandle? raw; int rawLatency;
    public static ChunkSource Open(string spec, string? index, int latencyMs)
    {
        var s = new ChunkSource();
        if (spec.StartsWith("art:")) { s.Idx = ChunkIndex.Load(index!); string name = spec[4..]; s.Data = new HttpRange(() => Art.SignedUrl(name)); }
        else if (spec.StartsWith("file:")) { s.Idx = ChunkIndex.Load(index!); s.Data = new FileRange(spec[5..], latencyMs); }
        else { string p = spec.StartsWith("raw:") ? spec[4..] : spec; s.RawPath = p; s.raw = File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.RandomAccess); s.ImageSize = new FileInfo(p).Length; s.rawLatency = latencyMs; return s; }
        s.ImageSize = s.Idx.ImageSize; s.ChunkSize = s.Idx.ChunkSize; return s;
    }
    public long WireBytes => HttpRange.Wire + FileRange.Wire;
    public long Gets => HttpRange.Gets + FileRange.Gets;
    public long Chunks => (ImageSize + ChunkSize - 1) / ChunkSize;
    // Reads chunks [first, first+count) as one contiguous decompressed buffer, with ONE range request.
    public byte[] Fetch(long first, int count)
    {
        long end = Math.Min(Chunks, first + count); long outLen = Math.Min(ImageSize, end * ChunkSize) - first * ChunkSize;
        if (Idx == null)
        {
            var b = new byte[outLen]; int got = 0; while (got < b.Length) got += RandomAccess.Read(raw!, b.AsSpan(got), first * ChunkSize + got);
            if (rawLatency > 0) Thread.Sleep(rawLatency); return b;
        }
        var (o0, _, _) = Idx.Entry(first); var (oL, lL, _) = Idx.Entry(end - 1); long total = oL + lL - o0;
        byte[] wire = total > 0 ? Data!.Read(o0, (int)total) : Array.Empty<byte>();
        var res = new byte[outLen];
        for (long i = first; i < end; i++)
        {
            var (o, l, rawc) = Idx.Entry(i); int dst = (int)((i - first) * ChunkSize); int want = (int)Idx.ChunkLen(i);
            if (l == 0) continue;   // zero chunk
            var src = wire.AsSpan((int)(o - o0), l);
            if (rawc) src.CopyTo(res.AsSpan(dst, want));
            else { if (!BrotliDecoder.TryDecompress(src, res.AsSpan(dst, want), out int w) || w != want) throw new Exception("decompress failed chunk " + i); }
        }
        return res;
    }
}

static class Pack
{
    public static int Run(string input, string data, string index, int chunkKiB, int quality)
    {
        int cs = chunkKiB * 1024; var sw = Stopwatch.StartNew();
        using var fin = File.OpenRead(input); long size = fin.Length; long n = (size + cs - 1) / cs;
        var idx = new ChunkIndex { ChunkSize = cs, ImageSize = size, E = new ulong[n + 1] };
        using var fo = new FileStream(data, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        long pos = 0, zero = 0, rawN = 0; const int B = 64;
        for (long b0 = 0; b0 < n; b0 += B)
        {
            int cnt = (int)Math.Min(B, n - b0); var inp = new byte[cnt][]; var outp = new byte[cnt][]; var isRaw = new bool[cnt];
            for (int j = 0; j < cnt; j++) { inp[j] = new byte[Math.Min(cs, size - (b0 + j) * cs)]; fin.ReadExactly(inp[j]); }
            Parallel.For(0, cnt, j =>
            {
                var s = inp[j];
                if (s.AsSpan().IndexOfAnyExcept((byte)0) < 0) { outp[j] = Array.Empty<byte>(); return; }
                var d = new byte[BrotliEncoder.GetMaxCompressedLength(s.Length)];
                if (BrotliEncoder.TryCompress(s, d, out int w, quality, 22) && w < s.Length) outp[j] = d.AsSpan(0, w).ToArray(); else { outp[j] = s; isRaw[j] = true; }
            });
            for (int j = 0; j < cnt; j++)
            {
                idx.E[b0 + j] = ((ulong)pos << 1) | (isRaw[j] ? 1UL : 0); fo.Write(outp[j]); pos += outp[j].Length;
                if (outp[j].Length == 0) zero++; if (isRaw[j]) rawN++;
            }
        }
        idx.E[n] = (ulong)pos << 1; idx.Save(index);
        Console.WriteLine($"packed {size >> 20} MiB -> {pos >> 20} MiB ({n} chunks, {zero} zero, {rawN} raw) in {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }
    // ranges <holes.txt "start length"> <imageSize> <out.ranges>: complement of holes, widened to chunk boundaries, merged ("start length" bytes)
    public static int Ranges(string holes, long imageSize, string outp, int cs = 1 << 20)
    {
        var h = new List<(long s, long e)>();
        foreach (var l in File.ReadLines(holes)) { var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries); if (p.Length >= 2) h.Add((long.Parse(p[0]), long.Parse(p[0]) + long.Parse(p[1]))); }
        h.Sort(); var meta = new List<(long s, long e)>(); long cur = 0;
        foreach (var x in h) { if (x.s > cur) meta.Add((cur, x.s)); cur = Math.Max(cur, x.e); }
        if (cur < imageSize) meta.Add((cur, imageSize));
        var o = new List<(long s, long e)>();
        foreach (var m in meta) { long s = m.s / cs * cs, e = Math.Min(imageSize, (m.e + cs - 1) / cs * cs); if (o.Count > 0 && s <= o[^1].e) o[^1] = (o[^1].s, Math.Max(o[^1].e, e)); else o.Add((s, e)); }
        File.WriteAllLines(outp, o.Select(r => $"{r.s} {r.e - r.s}")); long tot = o.Sum(r => r.e - r.s);
        Console.WriteLine($"{o.Count} ranges, {tot / 1048576.0:F1} MiB ({100.0 * tot / imageSize:F2}% of image)"); return 0;
    }
}
