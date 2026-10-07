// GitHub Actions artifact v4 client (Twirp) using the runner's ACTIONS_RUNTIME_TOKEN: upload as blocks, signed Range-capable download URL.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class Art
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    static string Token => Environment.GetEnvironmentVariable("ACTIONS_RUNTIME_TOKEN") ?? throw new Exception("no ACTIONS_RUNTIME_TOKEN");
    static string Base => (Environment.GetEnvironmentVariable("ACTIONS_RESULTS_URL") ?? throw new Exception("no ACTIONS_RESULTS_URL")).TrimEnd('/') + "/twirp/github.actions.results.api.v1.ArtifactService/";
    static (string run, string job) Ids()
    {
        var p = Token.Split('.')[1].Replace('-', '+').Replace('_', '/'); p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
        using var d = JsonDocument.Parse(Convert.FromBase64String(p));
        foreach (var s in d.RootElement.GetProperty("scp").GetString()!.Split(' ')) { var q = s.Split(':'); if (q.Length == 3 && q[0] == "Actions.Results") return (q[1], q[2]); }
        throw new Exception("no Actions.Results scope in token");
    }
    static JsonElement Call(string method, object body)
    {
        using var rq = new HttpRequestMessage(HttpMethod.Post, Base + method) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        rq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
        using var rs = http.Send(rq); var t = new StreamReader(rs.Content.ReadAsStream()).ReadToEnd();
        if (!rs.IsSuccessStatusCode) throw new Exception($"{method}: {(int)rs.StatusCode} {t}");
        return JsonDocument.Parse(t).RootElement.Clone();
    }
    public static string SignedUrl(string name)
    {
        var (r, j) = Ids();
        return Call("GetSignedArtifactURL", new { workflow_run_backend_id = r, workflow_job_run_backend_id = j, name }).GetProperty("signed_url").GetString()!;
    }
    static string Expiry() => DateTime.UtcNow.AddDays(int.Parse(Environment.GetEnvironmentVariable("CFLAZY_ARTIFACT_DAYS") ?? "1")).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
    // art-create: CreateArtifact and print the signed upload URL (a write-only block-blob SAS: Put Block / Put Block List only). For FastDownload (FASTDL_WRITE_ONLY_SAS=1).
    public static int Create(string name)
    {
        var (r, j) = Ids();
        var c = Call("CreateArtifact", new { workflow_run_backend_id = r, workflow_job_run_backend_id = j, name, version = 4, expires_at = Expiry() });
        if (!c.GetProperty("ok").GetBoolean()) throw new Exception("CreateArtifact not ok");
        Console.WriteLine(c.GetProperty("signed_upload_url").GetString()); return 0;
    }
    // art-finalize: FinalizeArtifact with the committed blob size.
    public static int Finalize(string name, long size)
    {
        var (r, j) = Ids();
        var f = Call("FinalizeArtifact", new { workflow_run_backend_id = r, workflow_job_run_backend_id = j, name, size = size.ToString() });
        if (!f.GetProperty("ok").GetBoolean()) throw new Exception("FinalizeArtifact not ok"); Console.WriteLine("finalized " + name + " " + size); return 0;
    }
    public static int Up(string name, string file)
    {
        var (r, j) = Ids(); var sw = System.Diagnostics.Stopwatch.StartNew();
        var c = Call("CreateArtifact", new { workflow_run_backend_id = r, workflow_job_run_backend_id = j, name, version = 4, expires_at = Expiry() });
        if (!c.GetProperty("ok").GetBoolean()) throw new Exception("CreateArtifact not ok");
        string url = c.GetProperty("signed_upload_url").GetString()!; long size = new FileInfo(file).Length; const int BS = 32 << 20;
        int nb = (int)((size + BS - 1) / BS); var ids = new string[nb];
        for (int i = 0; i < nb; i++) ids[i] = Convert.ToBase64String(Encoding.ASCII.GetBytes(i.ToString("D6")));
        Parallel.For(0, nb, new ParallelOptions { MaxDegreeOfParallelism = 6 }, i =>
        {
            var buf = new byte[Math.Min(BS, size - (long)i * BS)];
            using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) { f.Position = (long)i * BS; f.ReadExactly(buf); }
            for (int a = 0; ; a++)
            {
                try
                {
                    using var rq = new HttpRequestMessage(HttpMethod.Put, url + "&comp=block&blockid=" + Uri.EscapeDataString(ids[i])) { Content = new ByteArrayContent(buf) };
                    using var rs = http.Send(rq); if (!rs.IsSuccessStatusCode) throw new Exception("block " + i + " " + (int)rs.StatusCode); break;
                }
                catch when (a < 3) { Thread.Sleep(500); }
            }
        });
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>" + string.Concat(ids.Select(x => "<Latest>" + x + "</Latest>")) + "</BlockList>";
        using (var rq = new HttpRequestMessage(HttpMethod.Put, url + "&comp=blocklist") { Content = new StringContent(xml, Encoding.UTF8, "application/xml") })
        { using var rs = http.Send(rq); if (!rs.IsSuccessStatusCode) throw new Exception("blocklist " + (int)rs.StatusCode); }
        Call("FinalizeArtifact", new { workflow_run_backend_id = r, workflow_job_run_backend_id = j, name, size = size.ToString() });
        Console.WriteLine($"uploaded {name}: {size >> 20} MiB in {sw.Elapsed.TotalSeconds:F1}s ({size / 1048576.0 / sw.Elapsed.TotalSeconds:F0} MiB/s)"); return 0;
    }
    public static int Down(string name, string file)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); var u = SignedUrl(name);
        using var rs = http.Send(new HttpRequestMessage(HttpMethod.Get, u), HttpCompletionOption.ResponseHeadersRead); rs.EnsureSuccessStatusCode();
        using (var o = File.Create(file)) rs.Content.CopyTo(o, null, default);
        Console.WriteLine($"downloaded {name}: {new FileInfo(file).Length >> 20} MiB in {sw.Elapsed.TotalSeconds:F1}s"); return 0;
    }
}
