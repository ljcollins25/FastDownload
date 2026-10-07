// serve: Cloud Files provider. Guest-first scheduling, adjacent-chunk coalescing (one range GET per run of missing chunks),
// sequential read-ahead, metadata pre-hydration, throttled "sweep" (OS background full-hydration) queue.
using System.Diagnostics;
using System.Runtime.InteropServices;

static partial class Program
{
    const int CS = 1 << 20;
    static ChunkSource src = null!; static long srcLen; static bool[] sent = Array.Empty<bool>();
    sealed class Run { public Task<byte[]> T = null!; public long First; }
    static readonly Dictionary<long, Run> cache = new(); static readonly Queue<long> evict = new(); static readonly object cl = new();
    static int maxCoalesce = 8, readahead = 4, sweepDelay, sweepIdle = 100, sweepPromote, latency;
    static volatile bool stopping, prehydrating; static string drainFile = "";
    static StreamWriter log = null!; static readonly Stopwatch clock = Stopwatch.StartNew(); static readonly List<Cf.Callback> keep = new();
    static long guestBytes, sweepBytes, guestReqs, sweepReqs, promoted, raChunks, guestInflight, lastGuestTick, lastSweepDoneMs, lastSweepChunk = -1, lastEnd = -10, streak;
    static SemaphoreSlim guestSem = new(32);
    sealed class Pend { public long conn, transfer, rk, off, len; public long enq; public string tag = ""; }
    static readonly List<Pend> pend = new();
    static void W(string s) { lock (log) { log.WriteLine($"{clock.ElapsedMilliseconds},{s}"); log.Flush(); } }
    static string Who(IntPtr info) { try { IntPtr pi = Marshal.ReadIntPtr(info, 136); if (pi != IntPtr.Zero) { int pid = Marshal.ReadInt32(pi, 4); IntPtr ip = Marshal.ReadIntPtr(pi, 8); return pid + ":" + (ip == IntPtr.Zero ? "" : Path.GetFileName(Marshal.PtrToStringUni(ip))); } } catch { } return "kernel"; }
    static int Exec(uint type, long conn, long transfer, long reqKey, Action<IntPtr> fill, int paramSize)
    {
        IntPtr op = Marshal.AllocHGlobal(48), pr = Marshal.AllocHGlobal(96);
        for (int i = 0; i < 48; i += 8) Marshal.WriteInt64(op, i, 0); for (int i = 0; i < 96; i += 8) Marshal.WriteInt64(pr, i, 0);
        Marshal.WriteInt32(op, 0, 48); Marshal.WriteInt32(op, 4, (int)type); Marshal.WriteInt64(op, 8, conn); Marshal.WriteInt64(op, 16, transfer); Marshal.WriteInt64(op, 40, reqKey);
        Marshal.WriteInt32(pr, 0, paramSize); fill(pr);
        int hr = Cf.CfExecute(op, pr); Marshal.FreeHGlobal(op); Marshal.FreeHGlobal(pr); return hr;
    }

    // Decompressed bytes for chunks [c0,c1): missing chunks are fetched as runs of up to maxCoalesce adjacent chunks, ONE range request each.
    static byte[] GetData(long c0, long c1)
    {
        var refs = new Run[c1 - c0];
        lock (cl)
        {
            for (long c = c0; c < c1;)
            {
                if (cache.TryGetValue(c, out var ex)) { refs[c - c0] = ex; c++; continue; }
                long f = c; while (c < c1 && c - f < maxCoalesce && !cache.ContainsKey(c)) c++;
                int cnt = (int)(c - f); var run = new Run { First = f }; run.T = Task.Run(() => src.Fetch(f, cnt));
                for (long k = f; k < c; k++) { cache[k] = run; refs[k - c0] = run; }
            }
        }
        var res = new byte[Math.Min(srcLen, c1 * CS) - c0 * CS];
        try
        {
            for (long c = c0; c < c1; c++)
            {
                var r = refs[c - c0]; var buf = r.T.Result; int n = (int)Math.Min(CS, srcLen - c * CS);
                Buffer.BlockCopy(buf, (int)((c - r.First) * CS), res, (int)((c - c0) * CS), n);
            }
        }
        catch { lock (cl) { foreach (var r in refs) for (long k = r.First; cache.TryGetValue(k, out var x) && x == r; k++) cache.Remove(k); } throw; }
        return res;
    }
    static void Transfer(long conn, long transfer, long rk, long s, byte[] data, int status)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            int hr = Exec(0, conn, transfer, rk, pr => { Marshal.WriteInt32(pr, 8, 0); Marshal.WriteInt32(pr, 12, status); Marshal.WriteIntPtr(pr, 16, h.AddrOfPinnedObject()); Marshal.WriteInt64(pr, 24, s); Marshal.WriteInt64(pr, 32, data.Length); }, 40);
            if (hr != 0) throw new Exception($"CfExecute hr=0x{hr:X8}");
        }
        finally { h.Free(); }
    }
    // Answer one FETCH_DATA: requested chunks (+ read-ahead for guest) -> TRANSFER_DATA.
    static void Answer(long conn, long transfer, long rk, long reqOff, long reqLen, string cls, string tag)
    {
        var sw = Stopwatch.StartNew(); long c0 = reqOff / CS, c1 = (reqOff + reqLen + CS - 1) / CS; long chunks = (srcLen + CS - 1) / CS; if (c1 > chunks) c1 = chunks;
        long ra = 0;
        if (cls == "GUEST" && readahead > 0 && !prehydrating)
        {
            bool seq; lock (cl) { seq = c0 >= lastEnd - 1 && c0 <= lastEnd + 1; streak = seq ? streak + 1 : 0; lastEnd = Math.Max(c1, seq ? lastEnd : 0); if (!seq) lastEnd = c1; if (streak < 1) seq = false; }
            if (seq) { while (ra < readahead && c1 + ra < chunks && !sent[c1 + ra]) ra++; if (ra > 0) Interlocked.Add(ref raChunks, ra); }
        }
        long e1 = c1 + ra; string res = "";
        try
        {
            byte[] data;
            try { data = GetData(c0, e1); Transfer(conn, transfer, rk, c0 * CS, data, 0); }
            catch (Exception) when (ra > 0) { e1 = c1; ra = 0; data = GetData(c0, c1); Transfer(conn, transfer, rk, c0 * CS, data, 0); }
            lock (cl) { for (long k = c0; k < e1; k++) { sent[k] = true; evict.Enqueue(k); } while (evict.Count > 96) cache.Remove(evict.Dequeue()); }
            if (cls == "GUEST") { Interlocked.Add(ref guestBytes, data.Length); Interlocked.Increment(ref guestReqs); } else { Interlocked.Add(ref sweepBytes, data.Length); Interlocked.Increment(ref sweepReqs); }
            W($"FETCH_DATA,{cls},{tag},sent={c0 * CS}+{data.Length},ra={ra},{sw.Elapsed.TotalMilliseconds:F2}ms");
        }
        catch (Exception ex)
        {
            res = ex.Message; try { Transfer(conn, transfer, rk, reqOff, Array.Empty<byte>(), unchecked((int)0xC0000001)); } catch { }
            W($"FETCH_DATA,{cls},{tag},ERROR {res}");
        }
    }
    static void OnFetch(IntPtr info, IntPtr prm)
    {
        long conn = Marshal.ReadInt64(info, 8), transfer = Marshal.ReadInt64(info, 112), rk = Marshal.ReadInt64(info, 144);
        long reqOff = Marshal.ReadInt64(prm, 16), reqLen = Marshal.ReadInt64(prm, 24);
        string tag = $"flags=0x{Marshal.ReadInt32(prm, 8):X},req={reqOff}+{reqLen},by={Who(info)}";
        bool sweep = false;
        if (sweepDelay > 0 && !stopping && !prehydrating)
            lock (cl) { long ch = reqOff / CS, nx = Math.Max(0, lastSweepChunk + 1); while (nx < sent.Length && sent[nx]) nx++; if (ch == nx && reqOff > 4096) { sweep = true; lastSweepChunk = ch; } }
        if (sweep) { lock (pend) pend.Add(new Pend { conn = conn, transfer = transfer, rk = rk, off = reqOff, len = reqLen, enq = clock.ElapsedMilliseconds, tag = tag }); return; }
        GuestRun(conn, transfer, rk, reqOff, reqLen, "GUEST", tag);
    }
    static void GuestRun(long conn, long transfer, long rk, long off, long len, string cls, string tag)
    {
        guestSem.Wait(); Interlocked.Increment(ref guestInflight);
        try { Answer(conn, transfer, rk, off, len, cls, tag); }
        finally { Interlocked.Decrement(ref guestInflight); Volatile.Write(ref lastGuestTick, clock.ElapsedMilliseconds); guestSem.Release(); }
    }
    static void Sweeper()
    {
        while (true)
        {
            Pend? p = null; bool promote = false;
            lock (pend)
            {
                long now = clock.ElapsedMilliseconds; bool drain = stopping || (drainFile != "" && File.Exists(drainFile));
                if (pend.Count > 0)
                {
                    var h = pend[0];
                    if (drain) p = h;
                    else if (sweepPromote > 0 && now - h.enq >= sweepPromote) { p = h; promote = true; }
                    else if (Interlocked.Read(ref guestInflight) == 0 && now - Volatile.Read(ref lastGuestTick) >= sweepIdle && now - Volatile.Read(ref lastSweepDoneMs) >= sweepDelay) p = h;
                    if (p != null) pend.RemoveAt(0);
                }
            }
            if (p == null) { if (stopping) return; Thread.Sleep(5); continue; }
            if (promote) { Interlocked.Increment(ref promoted); long x = clock.ElapsedMilliseconds; var q = p; ThreadPool.QueueUserWorkItem(_ => GuestRun(q.conn, q.transfer, q.rk, q.off, q.len, "GUEST", "promoted," + q.tag)); continue; }
            Answer(p.conn, p.transfer, p.rk, p.off, p.len, "SWEEP", p.tag); Volatile.Write(ref lastSweepDoneMs, clock.ElapsedMilliseconds);
        }
    }
    static Cf.Callback Make(uint type) => (info, prm) =>
    {
        try
        {
            long conn = Marshal.ReadInt64(info, 8), transfer = Marshal.ReadInt64(info, 112), rk = Marshal.ReadInt64(info, 144);
            switch (type)
            {
                case 0: OnFetch(info, prm); return;
                case 1: { long off = Marshal.ReadInt64(prm, 16), len = Marshal.ReadInt64(prm, 24); Exec(2, conn, transfer, rk, pr => { Marshal.WriteInt32(pr, 8, 0); Marshal.WriteInt32(pr, 12, 0); Marshal.WriteInt64(pr, 16, off); Marshal.WriteInt64(pr, 24, len); }, 32); return; }
                case 3: Exec(4, conn, transfer, rk, pr => { Marshal.WriteInt32(pr, 8, 0); Marshal.WriteInt32(pr, 12, 0); Marshal.WriteInt64(pr, 16, 0); Marshal.WriteIntPtr(pr, 24, IntPtr.Zero); Marshal.WriteInt32(pr, 32, 0); }, 40); return;
                case 7: Exec(5, conn, transfer, rk, pr => { Marshal.WriteInt32(pr, 8, 0); Marshal.WriteInt32(pr, 12, 0); }, 40); return;
                case 9: Exec(6, conn, transfer, rk, pr => { Marshal.WriteInt32(pr, 8, 0); Marshal.WriteInt32(pr, 12, 0); }, 16); return;
                case 11: Exec(7, conn, transfer, rk, pr => { Marshal.WriteInt32(pr, 8, 0); Marshal.WriteInt32(pr, 12, 0); }, 16); return;
            }
        }
        catch (Exception ex) { W($"CB{type},EXC {ex.Message}"); }
    };

    static int Serve(string[] a)
    {
        string root = Path.GetFullPath(a[1]), name = Opt(a, "--name", "parent.vhd"), stop = Opt(a, "--stop", Path.Combine(root, "..", "cf.stop")), logp = Opt(a, "--log", Path.Combine(root, "..", "cf.log")), pre = Opt(a, "--prehydrate");
        drainFile = Opt(a, "--drain"); sweepDelay = int.Parse(Opt(a, "--sweep-delay-ms", "0")); sweepIdle = int.Parse(Opt(a, "--sweep-idle-ms", "100")); sweepPromote = int.Parse(Opt(a, "--sweep-promote-ms", "0"));
        maxCoalesce = int.Parse(Opt(a, "--coalesce", "8")); readahead = int.Parse(Opt(a, "--readahead", "4")); latency = int.Parse(Opt(a, "--latency-ms", "0"));
        guestSem = new SemaphoreSlim(int.Parse(Opt(a, "--guest-par", "32"))); int prePar = int.Parse(Opt(a, "--prehyd-par", "8"));
        ThreadPool.SetMinThreads(96, 96);
        Directory.CreateDirectory(root); log = new StreamWriter(new FileStream(logp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
        src = ChunkSource.Open(Opt(a, "--src"), Opt(a, "--index") is { Length: > 0 } ix ? ix : null, latency); srcLen = src.ImageSize; sent = new bool[(srcLen + CS - 1) / CS + 2];
        var reg = new Cf.SyncReg { StructSize = (uint)Marshal.SizeOf<Cf.SyncReg>(), ProviderName = Marshal.StringToHGlobalUni("CfLazy"), ProviderVersion = Marshal.StringToHGlobalUni("1.0"), ProviderId = new Guid("6d5e0b9a-7a8e-4a4a-9a0c-2c1d6a1f0003") };
        var pol = new Cf.SyncPolicies { StructSize = (uint)Marshal.SizeOf<Cf.SyncPolicies>(), HydPrimary = 0, HydModifier = 0, PopPrimary = 2 };
        int hr = Cf.CfRegisterSyncRoot(root, ref reg, ref pol, 0);
        Console.WriteLine($"args={string.Join(' ', a)}\nCfRegisterSyncRoot hr=0x{hr:X8}"); if (hr != 0) return 1;
        var table = new List<Cf.CallbackReg>(); for (uint t = 0; t <= 12; t++) { var d = Make(t); keep.Add(d); table.Add(new Cf.CallbackReg { Type = t, Callback = Marshal.GetFunctionPointerForDelegate(d) }); }
        table.Add(new Cf.CallbackReg { Type = 0xFFFFFFFF, Callback = IntPtr.Zero });
        hr = Cf.CfConnectSyncRoot(root, table.ToArray(), IntPtr.Zero, 0, out long key); Console.WriteLine($"CfConnectSyncRoot hr=0x{hr:X8}"); if (hr != 0) return 1;
        new Thread(Sweeper) { IsBackground = true }.Start();
        string ids = "id-" + name; var ident = Marshal.StringToHGlobalUni(ids); long now = DateTime.UtcNow.ToFileTimeUtc();
        var ph = new Cf.PlaceholderInfo { RelativeFileName = Marshal.StringToHGlobalUni(name), Creation = now, LastAccess = now, LastWrite = now, Change = now, Attributes = 0x80, FileSize = srcLen, FileIdentity = ident, FileIdentityLength = (uint)(2 * ids.Length), Flags = 2 };
        hr = Cf.CfCreatePlaceholders(root, ref ph, 1, 0, out uint done); Console.WriteLine($"CfCreatePlaceholders hr=0x{hr:X8} processed={done} entry=0x{ph.Result:X8} size={srcLen}");
        if (pre != "")
        {
            var sw = Stopwatch.StartNew(); long g0 = src.Gets, w0 = src.WireBytes; prehydrating = true;
            var ranges = File.ReadLines(pre).Select(l => l.Split(' ')).Where(p => p.Length == 2).Select(p => (s: long.Parse(p[0]), n: long.Parse(p[1]))).ToList();
            var pieces = new List<(long s, int n)>(); const int PIECE = 4 << 20;
            foreach (var r in ranges) for (long o = r.s; o < r.s + r.n; o += PIECE) pieces.Add((o, (int)Math.Min(PIECE, r.s + r.n - o)));
            long read = 0; string path = Path.Combine(root, name);
            Parallel.ForEach(pieces, new ParallelOptions { MaxDegreeOfParallelism = prePar }, pc =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.RandomAccess); var b = new byte[pc.n]; fs.Position = pc.s; fs.ReadExactly(b); Interlocked.Add(ref read, b.Length);
            });
            long present = 0, total = 0; foreach (var r in ranges) for (long c = r.s / CS; c < (r.s + r.n + CS - 1) / CS; c++) { total++; if (sent[c]) present++; }
            prehydrating = false;
            Console.WriteLine($"PREHYDRATE ranges={ranges.Count} read={read / 1048576.0:F1}MiB wire={(src.WireBytes - w0) / 1048576.0:F1}MiB gets={src.Gets - g0} present={present}/{total} chunks {sw.Elapsed.TotalSeconds:F2}s");
            lock (cl) { guestBytes = 0; guestReqs = 0; raChunks = 0; }
        }
        Console.WriteLine("READY"); Console.Out.Flush();
        while (!File.Exists(stop)) Thread.Sleep(200);
        stopping = true; Thread.Sleep(500);
        string sum = $"SUMMARY guestMiB={guestBytes / 1048576.0:F1} guestReqs={guestReqs} sweepMiB={sweepBytes / 1048576.0:F1} sweepReqs={sweepReqs} promoted={promoted} readaheadChunks={raChunks} httpGets={src.Gets} wireMiB={src.WireBytes / 1048576.0:F1} heldSweep={pend.Count}";
        W(sum); Console.WriteLine(sum);
        Cf.CfDisconnectSyncRoot(key); Cf.CfUnregisterSyncRoot(root); log.Close(); return 0;
    }
}
