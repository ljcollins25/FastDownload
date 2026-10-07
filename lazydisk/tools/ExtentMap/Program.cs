// ExtentMap: offline-ish NTFS extent map of a mounted volume, and a sparse "metadata only" copy of the image.
//   ExtentMap map <driveRoot e.g. V:> <partitionOffsetBytes> <imageSizeBytes> <holes.txt>
//      -> holes.txt: "start length" (image byte offsets) of non-resident file content + free clusters (merged); prints summary
//   ExtentMap sparsecopy <srcImage> <dstImage> [zeroBlockKiB=64]
//      -> dst = sparse (FSCTL_SET_SPARSE) copy of src where all-zero blocks are never written (so FastDownload --sparse-handling sees real data regions)
//   ExtentMap punch <srcImage> <dstImage> <holes.txt>
//      -> dst = sparse copy of src, hole ranges never written; everything else copied unbuffered (NO_BUFFERING|WRITE_THROUGH)
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
static class X
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref long inb, int ni, byte[] outb, int no, out int ret, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inb, int ni, IntPtr outb, int no, out int ret, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    static List<(long s, long e)> Merge(List<(long s, long e)> l)
    { l.Sort(); var o = new List<(long, long)>(); foreach (var r in l) { if (o.Count > 0 && r.s <= o[^1].Item2) o[^1] = (o[^1].Item1, Math.Max(o[^1].Item2, r.e)); else o.Add(r); } return o.Select(x => (x.Item1, x.Item2)).ToList(); }
    static int Main(string[] a)
    {
        if (a[0] == "map") return Map(a[1], long.Parse(a[2]), long.Parse(a[3]), a[4]);
        if (a[0] == "punch") return Punch(a[1], a[2], a[3]);
        if (a[0] == "sparsecopy") return SparseCopy(a[1], a[2], a.Length > 3 ? int.Parse(a[3]) : 64);
        return 2;
    }
    static int Map(string drive, long partOff, long imgSize, string outFile)
    {
        long cl = 4096; var content = new List<(long, long)>(); long files = 0, resident = 0, nonres = 0, errs = 0;
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        foreach (var f in new DirectoryInfo(drive + "\\").EnumerateFiles("*", opt))
        {
            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            files++;
            using var h = CreateFile(f.FullName, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h.IsInvalid) { errs++; continue; }
            long vcn = 0; bool any = false; var buf = new byte[64 * 1024];
            while (true)
            {
                long inb = vcn; bool ok = DeviceIoControl(h, 0x90073, ref inb, 8, buf, buf.Length, out int ret, IntPtr.Zero);
                int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err != 234) break;
                int n = BitConverter.ToInt32(buf, 0); long start = BitConverter.ToInt64(buf, 8);
                for (int i = 0; i < n; i++)
                {
                    long next = BitConverter.ToInt64(buf, 16 + i * 16), lcn = BitConverter.ToInt64(buf, 24 + i * 16);
                    if (lcn >= 0) { content.Add((partOff + lcn * cl, partOff + (lcn + (next - start)) * cl)); any = true; }
                    start = next;
                }
                if (ok) break; vcn = start;
            }
            if (any) nonres++; else resident++;
        }
        // free clusters from the volume bitmap
        var free = new List<(long, long)>();
        using (var v = CreateFile("\\\\.\\" + drive, 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            long lcn = 0; var buf = new byte[1 << 20];
            while (true)
            {
                bool ok = DeviceIoControl(v, 0x9006F, ref lcn, 8, buf, buf.Length, out int ret, IntPtr.Zero); int err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err != 234) { Console.WriteLine("bitmap err " + err); break; }
                long st = BitConverter.ToInt64(buf, 0), cnt = BitConverter.ToInt64(buf, 8); long runStart = -1;
                for (long i = 0; i < cnt; i++)
                {
                    bool used = (buf[16 + i / 8] >> (int)(i % 8) & 1) != 0;
                    if (!used && runStart < 0) runStart = i; if (used && runStart >= 0) { free.Add((partOff + (st + runStart) * cl, partOff + (st + i) * cl)); runStart = -1; }
                }
                if (runStart >= 0) free.Add((partOff + (st + runStart) * cl, partOff + (st + cnt) * cl));
                if (ok) break; lcn = st + cnt;
            }
        }
        var c = Merge(content); var fr = Merge(free); var holes = Merge(content.Concat(free).ToList());
        File.WriteAllLines(outFile, holes.Select(h => $"{h.s} {h.e - h.s}"));
        long cb = c.Sum(x => x.e - x.s), fb = fr.Sum(x => x.e - x.s), hb = holes.Sum(x => x.e - x.s);
        File.WriteAllLines(outFile + ".content", c.Select(h => $"{h.s} {h.e - h.s}"));
        Console.WriteLine($"files={files} resident(no extents)={resident} nonresident={nonres} openErrors={errs}");
        Console.WriteLine($"content: {c.Count} ranges, {cb / 1048576.0:F1} MiB; free: {fr.Count} ranges, {fb / 1048576.0:F1} MiB; holes(content+free) {holes.Count} ranges {hb / 1048576.0:F1} MiB");
        Console.WriteLine($"image {imgSize / 1048576.0:F1} MiB; metadata set (image - holes) = {(imgSize - hb) / 1048576.0:F1} MiB = {(imgSize - hb) * 100.0 / imgSize:F2}%");
        return 0;
    }
    static unsafe int Punch(string src, string dst, string holesFile)
    {
        var holes = File.ReadAllLines(holesFile).Select(l => l.Split(' ')).Select(p => (s: long.Parse(p[0]), e: long.Parse(p[0]) + long.Parse(p[1]))).OrderBy(x => x.s).ToList();
        using var s = File.OpenHandle(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long len = RandomAccess.GetLength(s);
        using var d = File.OpenHandle(dst, FileMode.Create, FileAccess.ReadWrite, FileShare.None, (FileOptions)0x20000000 | FileOptions.WriteThrough);
        DeviceIoControl(d, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);   // FSCTL_SET_SPARSE
        RandomAccess.SetLength(d, len);
        const int CH = 1 << 20; byte* p = (byte*)NativeMemory.AlignedAlloc(CH, 4096); var sp = new Span<byte>(p, CH);
        long copied = 0, pos = 0; int hi = 0; long end = len & ~4095L;
        while (pos < end)
        {
            while (hi < holes.Count && holes[hi].e <= pos) hi++;
            long next = end;
            if (hi < holes.Count && holes[hi].s <= pos) { pos = Math.Min(holes[hi].e, end); continue; }   // inside a hole: skip
            if (hi < holes.Count) next = Math.Min(next, holes[hi].s);
            long n = Math.Min(next - pos, CH); int r = RandomAccess.Read(s, sp.Slice(0, (int)n), pos);
            RandomAccess.Write(d, sp.Slice(0, (int)n), pos); copied += n; pos += n;
        }
        if (len > end) { var t = sp.Slice(0, 512); RandomAccess.Read(s, t, len - 512); RandomAccess.Write(d, t, len - 512); copied += 512; }  // VHD footer
        Console.WriteLine($"punched: copied {copied / 1048576.0:F1} MiB of {len / 1048576.0:F1} MiB");
        return 0;
    }

    static unsafe int SparseCopy(string src, string dst, int blockKiB)
    {
        using var s = File.OpenHandle(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, FileOptions.SequentialScan);
        long len = RandomAccess.GetLength(s);
        using var d = File.OpenHandle(dst, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        DeviceIoControl(d, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);   // FSCTL_SET_SPARSE
        RandomAccess.SetLength(d, len);
        int B = blockKiB * 1024, CH = Math.Max(B, 4 << 20) / B * B; var buf = new byte[CH]; long pos = 0, written = 0;
        while (pos < len)
        {
            int n = RandomAccess.Read(s, buf.AsSpan(0, (int)Math.Min(CH, len - pos)), pos); if (n <= 0) break;
            for (int o = 0; o < n; o += B) { int m = Math.Min(B, n - o); var blk = buf.AsSpan(o, m); if (blk.IndexOfAnyExcept((byte)0) >= 0) { RandomAccess.Write(d, blk, pos + o); written += m; } }
            pos += n;
        }
        Console.WriteLine($"sparsecopy: wrote {written / 1048576.0:F1} MiB of {len / 1048576.0:F1} MiB");
        return 0;
    }
}
