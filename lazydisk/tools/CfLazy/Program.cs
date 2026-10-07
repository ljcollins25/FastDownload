// CfLazy: Cloud Files provider serving a VHD parent placeholder from a remote chunk store (Actions artifact), plus store tools.
//   CfLazy pack <input> <data> <index> [chunkKiB=1024] [brotliQ=1]     CfLazy ranges <holes> <imageSize> <out.ranges>
//   CfLazy up|down <artifactName> <file>                                CfLazy bench --src <src> [--index f] [--n 64] [--par 16] [--latency-ms N]
//   CfLazy art-create <name> | art-finalize <name> <size> | art-url <name>   (artifact helpers for FastDownload uploads)
//   CfLazy fdidx <fastdownload.manifest.json> <out.idx>   (lazy reads of a FastDownload blob: 1 MiB, Brotli, SkipHoles)
//   CfLazy serve <syncRootDir> --src <art:name|file:data.bin|raw path> --index f --name parent.vhd [--prehydrate ranges] [--stop f] [--drain f] [--log f] ...
using System.Diagnostics;

static partial class Program
{
    static string Opt(string[] a, string k, string d = "") { for (int i = 0; i + 1 < a.Length; i++) if (a[i] == k) return a[i + 1]; return d; }
    static int Main(string[] a)
    {
        if (a.Length == 0) { Console.Error.WriteLine("usage: CfLazy pack|ranges|up|down|bench|serve ..."); return 2; }
        switch (a[0])
        {
            case "pack": return Pack.Run(a[1], a[2], a[3], a.Length > 4 ? int.Parse(a[4]) : 1024, a.Length > 5 ? int.Parse(a[5]) : 1);
            case "ranges": return Pack.Ranges(a[1], long.Parse(a[2]), a[3]);
            case "up": return Art.Up(a[1], a[2]);
            case "down": return Art.Down(a[1], a[2]);
            case "art-create": return Art.Create(a[1]);                       // print a write-only upload SAS for a new artifact (FastDownload --uri)
            case "art-finalize": return Art.Finalize(a[1], long.Parse(a[2]));  // finalize an artifact whose blob was written through that SAS
            case "art-url": Console.WriteLine(Art.SignedUrl(a[1])); return 0;  // signed, Range-capable read URL
            case "fdidx": return FdIndex.Run(a[1], a[2]);                      // FastDownload manifest -> CFIX chunk index (see FdIndex.cs)
            case "bench": return Bench(a);
            case "serve": return Serve(a);
        }
        return 2;
    }
    static int Bench(string[] a)
    {
        var src = ChunkSource.Open(Opt(a, "--src"), Opt(a, "--index") is { Length: > 0 } ix ? ix : null, int.Parse(Opt(a, "--latency-ms", "0")));
        int n = int.Parse(Opt(a, "--n", "64")), par = int.Parse(Opt(a, "--par", "16")); long total = src.Chunks; var rnd = new Random(5);
        var lat = new List<double>(); for (int i = 0; i < Math.Min(n, 32); i++) { var s = Stopwatch.StartNew(); src.Fetch(rnd.NextInt64(total), 1); lat.Add(s.Elapsed.TotalMilliseconds); }
        lat.Sort(); Console.WriteLine($"sequential single-chunk p50/p95 {lat[lat.Count / 2]:F0}/{lat[(int)(lat.Count * 0.95)]:F0} ms");
        foreach (var (cnt, p) in new[] { (1, par), (8, 4), (8, par / 2) })
        {
            long w0 = src.WireBytes; var sw = Stopwatch.StartNew(); long bytes = 0; int reqs = n / cnt;
            Parallel.For(0, reqs, new ParallelOptions { MaxDegreeOfParallelism = p }, i => { var b = src.Fetch(rnd.NextInt64(total - cnt), cnt); Interlocked.Add(ref bytes, b.Length); });
            double t = sw.Elapsed.TotalSeconds; Console.WriteLine($"{cnt}-chunk requests x{reqs}, par {p}: {bytes / 1048576.0 / t:F1} MiB/s decompressed, {(src.WireBytes - w0) / 1048576.0 / t:F1} MiB/s wire");
        }
        return 0;
    }
}

