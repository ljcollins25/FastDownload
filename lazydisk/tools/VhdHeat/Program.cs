// VhdHeat: real-time ETW consumer of Microsoft-Windows-VHDMP "Starting an IO" (event 1001: file, op, offset, size).
// Keeps a per-chunk heat map (last-read tick, read count) of guest reads on one VHD, prints a summary every few seconds.
// usage: VhdHeat <child-file-substring> [chunkMiB=1] [reportSec=5] [stopFile]
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System.Diagnostics;

string match = args[0]; int chunkMiB = args.Length > 1 ? int.Parse(args[1]) : 1; int rep = args.Length > 2 ? int.Parse(args[2]) : 5; string stopFile = args.Length > 3 ? args[3] : "";
var typeN = new long[16]; var typeB = new long[16]; var lastRead = new Dictionary<long, long>(); var count = new Dictionary<long, int>();
long events = 0, reads = 0, writes = 0, unmaps = 0, readBytes = 0, other = 0; var clock = Stopwatch.StartNew();
var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
using var sess = new TraceEventSession("VhdHeatSession");
sess.StopOnDispose = true;
sess.EnableProvider("Microsoft-Windows-VHDMP", TraceEventLevel.Verbose, ulong.MaxValue);
var parser = new RegisteredTraceEventParser(sess.Source);
parser.All += (TraceEvent e) =>
{
    if ((int)e.ID != 1001) return;
    events++;
    string file = (string)e.PayloadByName("VhdId")!; int ty = (int)e.PayloadByName("VhdIoType")!; long off = Convert.ToInt64(e.PayloadByName("Offset")), size = Convert.ToInt64(e.PayloadByName("Length"));
    if (!file.Contains(match, StringComparison.OrdinalIgnoreCase)) return;
    typeN[ty & 15]++; typeB[ty & 15] += size;
    if (ty == 2) { reads++; readBytes += size; long c0 = off / (chunkMiB << 20), c1 = (off + Math.Max(size, 1) - 1) / (chunkMiB << 20); for (long c = c0; c <= c1; c++) { lastRead[c] = e.TimeStampRelativeMSec > 0 ? (long)e.TimeStampRelativeMSec : clock.ElapsedMilliseconds; count[c] = count.GetValueOrDefault(c) + 1; } }
    else if (ty == 1) writes++; else if (ty == 8) unmaps++; else other++;
};
var t = new Thread(() =>
{
    long pe = 0, pr = 0;
    while (true)
    {
        Thread.Sleep(rep * 1000);
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds;
        Console.WriteLine($"t={clock.Elapsed.TotalSeconds:F0}s events={events} (+{(events - pe) / rep}/s) reads={reads} (+{(reads - pr) / rep}/s) writes={writes} unmaps={unmaps} readMiB={readBytes >> 20} chunks(heat)={lastRead.Count} cpuMs={cpu:F0} ({cpu / clock.ElapsedMilliseconds * 100:F2}% of one core)");
        pe = events; pr = reads;
        if (stopFile != "" && File.Exists(stopFile)) { sess.Dispose(); return; }
    }
}) { IsBackground = true };
t.Start();
Console.WriteLine("READY");
sess.Source.Process();
for (int i = 0; i < 16; i++) if (typeN[i] > 0) Console.WriteLine($"VhdIoType {i}: n={typeN[i]} MiB={typeB[i] >> 20}");
Console.WriteLine($"FINAL events={events} reads={reads} distinctChunks={lastRead.Count} cpuMs={(Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalMilliseconds:F0} wallMs={clock.ElapsedMilliseconds}");
