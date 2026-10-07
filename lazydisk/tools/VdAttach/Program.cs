// VdAttach <file> <openFlagsHex> <attachFlagsHex> [--ver 2|3] [--type 0|2|3] [--hold stopFile] [--access hex]
// OpenVirtualDisk (v2/v3 params) + AttachVirtualDisk with caller-chosen flags.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

static class V
{
    [StructLayout(LayoutKind.Sequential)] public struct StorageType { public uint DeviceId; public Guid VendorId; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] public struct OpenParams { public int Version; public int GetInfoOnlyOrRW; public int ReadOnly; public Guid Resiliency; public Guid Snapshot; }
    [StructLayout(LayoutKind.Sequential)] public struct AttachParams { public int Version; public uint Reserved; }
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)] public static extern int OpenVirtualDisk(ref StorageType t, string path, uint access, uint flags, ref OpenParams p, out IntPtr h);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)] public static extern int AttachVirtualDisk(IntPtr h, IntPtr sd, uint flags, uint prov, ref AttachParams p, IntPtr ov);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)] public static extern int DetachVirtualDisk(IntPtr h, uint flags, uint prov);
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)] public static extern int GetVirtualDiskPhysicalPath(IntPtr h, ref uint size, StringBuilder path);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct Prog { public uint Status; public ulong Cur; public ulong Total; }
    [DllImport("virtdisk.dll")] public static extern int MirrorVirtualDisk(IntPtr h, uint flags, IntPtr prm, IntPtr ov);
    [DllImport("virtdisk.dll")] public static extern int BreakMirrorVirtualDisk(IntPtr h);
    [DllImport("virtdisk.dll")] public static extern int GetVirtualDiskOperationProgress(IntPtr h, IntPtr ov, out Prog p);
    [DllImport("virtdisk.dll")] public static extern int SetVirtualDiskInformation(IntPtr h, IntPtr info);
    [DllImport("virtdisk.dll")] public static extern int MergeVirtualDisk(IntPtr h, uint flags, IntPtr prm, IntPtr ov);
    [DllImport("kernel32.dll")] public static extern IntPtr CreateEventW(IntPtr a, bool m, bool s, string? n);
    public static IntPtr Ov() { IntPtr p = Marshal.AllocHGlobal(32); for (int i = 0; i < 32; i++) Marshal.WriteByte(p, i, 0); Marshal.WriteIntPtr(p, 24, CreateEventW(IntPtr.Zero, true, false, null)); return p; }
    // ops file: one command per line: mirror <flagsHex> <path> | wait [seconds] | break | setparent <path> | setparentid <guid> | merge <src> <tgt> | sleep <ms> | quit
    public static void Ops(IntPtr h, string opsFile, string outFile)
    {
        IntPtr ov = Ov(); int done = 0;
        void Say(string s) { File.AppendAllText(outFile, s + "\n"); Console.WriteLine(s); Console.Out.Flush(); }
        while (true)
        {
            if (!File.Exists(opsFile)) { Thread.Sleep(200); continue; }
            string[] lines; try { lines = File.ReadAllLines(opsFile); } catch { Thread.Sleep(100); continue; }
            for (; done < lines.Length; done++)
            {
                var t = lines[done].Split(' ', 3); string c = t[0];
                if (c == "quit") return;
                else if (c == "mirror") { IntPtr p = Marshal.AllocHGlobal(16); Marshal.WriteInt32(p, 0, 1); Marshal.WriteInt32(p, 4, 0); Marshal.WriteIntPtr(p, 8, Marshal.StringToHGlobalUni(t[2])); Say($"OPS mirror flags=0x{t[1]} {t[2]} rc={MirrorVirtualDisk(h, Convert.ToUInt32(t[1], 16), p, ov)}"); }
                else if (c == "wait") { int secs = t.Length > 1 ? int.Parse(t[1]) : 60; var sw = Stopwatch.StartNew(); Prog pg = default; int r = 0; while (sw.Elapsed.TotalSeconds < secs) { r = GetVirtualDiskOperationProgress(h, ov, out pg); if (r != 0 || (pg.Status != 997 && pg.Status != 0) || (pg.Total > 0 && pg.Cur >= pg.Total)) break; Thread.Sleep(200); } Say($"OPS progress rc={r} status={pg.Status} cur={pg.Cur} total={pg.Total} after {sw.ElapsedMilliseconds} ms"); }
                else if (c == "break") Say($"OPS break rc={BreakMirrorVirtualDisk(h)}");
                else if (c == "setparent") { IntPtr p = Marshal.AllocHGlobal(16); Marshal.WriteInt32(p, 0, 1); Marshal.WriteInt32(p, 4, 0); Marshal.WriteIntPtr(p, 8, Marshal.StringToHGlobalUni(t[1] + (t.Length > 2 ? " " + t[2] : ""))); Say($"OPS setparent rc={SetVirtualDiskInformation(h, p)}"); }
                else if (c == "setparentid") { IntPtr p = Marshal.AllocHGlobal(32); for (int i = 0; i < 32; i++) Marshal.WriteByte(p, i, 0); Marshal.WriteInt32(p, 0, 2); Marshal.Copy(Guid.Parse(t[1]).ToByteArray(), 0, p + 8, 16); Say($"OPS setparentid rc={SetVirtualDiskInformation(h, p)}"); }
                else if (c == "merge") { IntPtr p = Marshal.AllocHGlobal(16); Marshal.WriteInt32(p, 0, 2); Marshal.WriteInt32(p, 4, int.Parse(t[1])); Marshal.WriteInt32(p, 8, int.Parse(t[2])); Say($"OPS merge2 src={t[1]} tgt={t[2]} rc={MergeVirtualDisk(h, 0, p, ov)}"); }
                else if (c == "sleep") Thread.Sleep(int.Parse(t[1]));
                else Say("OPS unknown " + c);
            }
            Thread.Sleep(200);
        }
    }
}

static class P
{
    static int Main(string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("usage: VdAttach <file> <openHex> <attachHex> [--ver 2|3] [--type 0|2|3] [--hold stopFile] [--access hex]"); return 2; }
        string file = a[0]; uint of = Convert.ToUInt32(a[1], 16), af = Convert.ToUInt32(a[2], 16);
        string? ops = null; Guid res = Guid.Empty, snap = Guid.Empty; int ver = 2; uint type = 2; string? hold = null; uint? access = null;
        for (int i = 3; i + 1 < a.Length; i += 2) switch (a[i]) { case "--ver": ver = int.Parse(a[i + 1]); break; case "--type": type = uint.Parse(a[i + 1]); break; case "--hold": hold = a[i + 1]; break; case "--access": access = Convert.ToUInt32(a[i + 1], 16); break; case "--ops": ops = a[i + 1]; break; case "--res": res = Guid.Parse(a[i + 1]); break; case "--snap": snap = Guid.Parse(a[i + 1]); break; }
        bool ro = (af & 1) != 0;
        uint acc = access ?? 0;   // v2/v3 open parameters require VIRTUAL_DISK_ACCESS_NONE
        var st = new V.StorageType { DeviceId = type, VendorId = new Guid("EC984AEC-A0F9-47e9-901F-71415A66345B") };
        var op = new V.OpenParams { Version = ver, ReadOnly = ro ? 1 : 0, Resiliency = res, Snapshot = snap };   // v2: GetInfoOnly=0, ReadOnly
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"open flags=0x{of:X} attach=0x{af:X} ver={ver} access=0x{acc:X}");
        int rc = V.OpenVirtualDisk(ref st, file, acc, of, ref op, out var h);
        Console.WriteLine($"OpenVirtualDisk rc={rc} (0x{rc:X8}) {sw.ElapsedMilliseconds} ms"); Console.Out.Flush();
        if (rc != 0) return 10;
        var ap = new V.AttachParams { Version = 1 };
        var sw2 = Stopwatch.StartNew();
        rc = V.AttachVirtualDisk(h, IntPtr.Zero, af, 0, ref ap, IntPtr.Zero);
        Console.WriteLine($"AttachVirtualDisk rc={rc} (0x{rc:X8}) {sw2.ElapsedMilliseconds} ms"); Console.Out.Flush();
        if (rc == 0)
        {
            uint sz = 520; var sb = new StringBuilder(260); int r2 = V.GetVirtualDiskPhysicalPath(h, ref sz, sb);
            Console.WriteLine($"physical path rc={r2} {sb}"); Console.WriteLine("ATTACHED"); Console.Out.Flush();
            if (ops != null) V.Ops(h, ops, ops + ".out");
            if (hold != null) { while (!File.Exists(hold)) Thread.Sleep(200); var s3 = Stopwatch.StartNew(); int d = V.DetachVirtualDisk(h, 0, 0); Console.WriteLine($"Detach rc={d} {s3.ElapsedMilliseconds} ms"); }
        }
        V.CloseHandle(h);
        return rc == 0 ? 0 : 11;
    }
}
