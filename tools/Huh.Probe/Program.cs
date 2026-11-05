using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Huh.Core.Detection;
using Huh.Core.Dialogs;

// Huh.Probe watch [--verbose]          live JSONL of every detected dialog
// Huh.Probe score [--only a,b] [--harness path] [--out dir]
//                                      drive every harness scenario, report hit rate / false positives
var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(), new NintHexConverter() } };
string mode = args.FirstOrDefault() ?? "watch";
bool verbose = args.Contains("--verbose");
string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

string RepoPath(params string[] parts) => Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));

if (mode == "windows" && args.Length > 1)
{
    // Diagnostics: visible top-level windows of every process with this name (class, title, owner, ex-style).
    var pids = Process.GetProcessesByName(args[1]).Select(p => p.Id).ToHashSet();
    Diag.EnumWindows((h, _) =>
    {
        Diag.GetWindowThreadProcessId(h, out var pid);
        if (pids.Contains((int)pid) && Diag.IsWindowVisible(h))
        {
            var cls = new StringBuilder(256); Diag.GetClassNameW(h, cls, 256);
            var title = new StringBuilder(512); Diag.GetWindowTextW(h, title, 512);
            Console.WriteLine($"0x{h:X} pid={pid} class='{cls}' title='{title}' owner=0x{Diag.GetWindow(h, 4):X} ex=0x{Diag.GetWindowLongW(h, -20):X}");
        }
        return true;
    }, 0);
    return 0;
}

if (mode == "uia" && args.Length > 1)
{
    // Diagnostics: dump the UIA subtree of a window (hex hwnd).
    var reader = new Huh.Core.Dialogs.UiaReader();
    var hwndArg = (nint)Convert.ToInt64(args[1], 16);
    if (args.Contains("--find"))
    {
        var sw = Stopwatch.StartNew();
        var found = reader.FindEmbeddedDialog(hwndArg, out var depth);
        Console.WriteLine($"depth={depth}");
        Console.WriteLine($"FindEmbeddedDialog: {(found is null ? "null" : $"'{Huh.Core.Interop.Native.Take(found.get_CurrentName())}' bounds={Huh.Core.Dialogs.UiaReader.Bounds(found)}")} in {sw.ElapsedMilliseconds} ms");
        return 0;
    }
    var snap = reader.Read(hwndArg);
    Console.WriteLine($"root='{snap.RootName}' isDialog={snap.IsDialog} error={snap.Error} nodes={snap.Nodes.Count}");
    foreach (var n in snap.Nodes)
        if (n.Name.Length > 0 || n.ControlType is "Window" or "Pane" or "Group")
            Console.WriteLine($"  {n.ControlType,-12} '{(n.Name.Length > 90 ? n.Name[..90] + "…" : n.Name)}' cls={n.ClassName} id={n.AutomationId}");
    return 0;
}

if (mode == "snap")
{
    return BubbleCheck.Snap(Opt("--out") ?? RepoPath("artifacts", "m3", "snap.png"),
        int.Parse(Opt("--timeout") ?? "20000"), int.Parse(Opt("--settle") ?? "900"));
}

if (mode == "bubble")
{
    return BubbleCheck.Run(
        Opt("--harness") ?? RepoPath("tools", "Huh.TestHarness", "bin", "Debug", "net10.0-windows", "Huh.TestHarness.exe"),
        Opt("--app") ?? RepoPath("src", "Huh.App", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "Huh.exe"),
        Opt("--out") ?? RepoPath("artifacts", "m2"));
}

using var monitor = new DialogMonitor();
var detections = new BlockingCollection<DialogInfo>();
monitor.DialogDetected += d => detections.Add(d);
monitor.DialogClosed += h => { if (verbose) Console.Error.WriteLine($"closed 0x{h:X}"); };
monitor.Log += m => Console.Error.WriteLine($"[log] {m}");
monitor.Start();

if (mode == "watch")
{
    Console.Error.WriteLine("Watching for dialogs. Ctrl+C to stop.");
    foreach (var d in detections.GetConsumingEnumerable())
        Console.WriteLine(JsonSerializer.Serialize(verbose ? d : d with { Nodes = [] }, json));
    return 0;
}

if (mode != "score") { Console.Error.WriteLine($"unknown mode {mode}"); return 2; }

// ------------------------------------------------------------------ score
string harness = Opt("--harness") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
    "..", "..", "..", "..", "Huh.TestHarness", "bin", "Debug", "net10.0-windows", "Huh.TestHarness.exe"));
if (!File.Exists(harness)) { Console.Error.WriteLine($"harness not found: {harness}"); return 2; }
string outDir = Opt("--out") ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts"));
Directory.CreateDirectory(outDir);

var scenarios = JsonSerializer.Deserialize<List<ScenarioRow>>(RunCapture(harness, "list"), json)!;
var only = Opt("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
if (only is not null) scenarios = scenarios.Where(s => only.Contains(s.Id)).ToList();

var results = new List<ScenarioResult>();
foreach (var s in scenarios)
{
    while (detections.TryTake(out _)) { } // drain stragglers from the previous scenario
    var started = DateTimeOffset.Now;
    using var p = Process.Start(new ProcessStartInfo(harness, $"run {s.Id}") { UseShellExecute = false, CreateNoWindow = true })!;
    var timeout = TimeSpan.FromSeconds(s.ExpectError ? 6 : 3);
    var sw = Stopwatch.StartNew();
    DialogInfo? hit = null;
    while (sw.Elapsed < timeout && hit is null)
    {
        if (!detections.TryTake(out var d, 200)) continue;
        bool mine = d.Process.Pid == p.Id
            || (s.ExpectedProcess is not null && string.Equals(d.Process.Name, s.ExpectedProcess, StringComparison.OrdinalIgnoreCase) && d.DetectedAt >= started);
        if (mine) hit = d;
    }

    bool correct = s.ExpectError ? hit?.IsError == true : hit?.IsError != true;
    results.Add(new ScenarioResult(s, hit, correct));
    Console.WriteLine($"{(correct ? "PASS" : "FAIL"),-5} {s.Id,-30} {(hit is null ? "no detection" : $"{hit.Severity,-11} {hit.Kind,-11} {hit.LatencyMs,6:F0} ms  [{string.Join(' ', hit.Signals)}]")}");
    if (verbose && hit is not null)
        foreach (var n in hit.Nodes) Console.WriteLine($"        {n.ControlType,-12} '{Trunc(n.Name, 70)}' cls={n.ClassName} id={n.AutomationId}");

    try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { }
    if (hit is { Hwnd: not 0 } && IsWindow(hit.Hwnd)) PostMessage(hit.Hwnd, 0x0010 /*WM_CLOSE*/, 0, 0); // dialogs owned by other processes (csrss)
    Thread.Sleep(400);
}

// ------------------------------------------------------------------ report
var pos = results.Where(r => r.Scenario.ExpectError).ToList();
var neg = results.Where(r => !r.Scenario.ExpectError).ToList();
int tp = pos.Count(r => r.Correct), fn = pos.Count - tp;
int fp = neg.Count(r => !r.Correct), tn = neg.Count - fp;
int detectedAny = pos.Count(r => r.Hit is not null);
var lat = pos.Where(r => r.Hit is not null).Select(r => r.Hit!.LatencyMs).Order().ToList();
double P(double q) => lat.Count == 0 ? 0 : lat[(int)Math.Min(lat.Count - 1, Math.Round(q * (lat.Count - 1)))];

var md = new StringBuilder();
md.AppendLine($"# M1 detection report ({DateTime.Now:yyyy-MM-dd HH:mm})\n");
md.AppendLine($"- Error dialogs: **{tp}/{pos.Count} detected and classified as error ({100.0 * tp / Math.Max(1, pos.Count):F0}%)**; {detectedAny}/{pos.Count} detected at all");
md.AppendLine($"- Non-error dialogs: {tn}/{neg.Count} correctly ignored, {fp} false positive(s)");
md.AppendLine($"- Latency (event to classified), detected error dialogs: p50 {P(0.5):F0} ms, p95 {P(0.95):F0} ms, max {(lat.Count > 0 ? lat[^1] : 0):F0} ms\n");
md.AppendLine("| Result | Scenario | Expected | Got | Kind | Process | Latency | Signals |");
md.AppendLine("|---|---|---|---|---|---|---|---|");
foreach (var r in results)
{
    var h = r.Hit;
    md.AppendLine($"| {(r.Correct ? "PASS" : "**FAIL**")} | `{r.Scenario.Id}` | {(r.Scenario.ExpectError ? "error" : "not error")} | {(h is null ? "not detected" : h.Severity)} | {h?.Kind} | {h?.Process.Name} | {(h is null ? "" : $"{h.LatencyMs:F0} ms")} | {(h is null ? "" : string.Join(" ", h.Signals).Replace("|", "\\|"))} |");
}
File.WriteAllText(Path.Combine(outDir, "m1-report.md"), md.ToString());
File.WriteAllLines(Path.Combine(outDir, "m1-detections.jsonl"), results.Where(r => r.Hit is not null).Select(r => JsonSerializer.Serialize(new { r.Scenario.Id, r.Hit }, json)));
Console.WriteLine();
Console.WriteLine(md.ToString().Split("\n| Result")[0]);
Console.WriteLine($"Report: {Path.Combine(outDir, "m1-report.md")}");
return fn + fp == 0 ? 0 : 1;

static string RunCapture(string exe, string args)
{
    using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false })!;
    var s = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return s;
}

static string Trunc(string s, int n) => (s.Length <= n ? s : s[..n] + "…").Replace("\n", "⏎").Replace("\r", "");

[DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
[DllImport("user32.dll")] static extern bool PostMessage(nint hwnd, uint msg, nint w, nint l);

internal sealed record ScenarioRow(string Id, bool ExpectError, string? ExpectedProcess, string Description);
internal sealed record ScenarioResult(ScenarioRow Scenario, DialogInfo? Hit, bool Correct);

internal sealed class NintHexConverter : JsonConverter<nint>
{
    public override nint Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o) => (nint)Convert.ToInt64(r.GetString(), 16);
    public override void Write(Utf8JsonWriter w, nint v, JsonSerializerOptions o) => w.WriteStringValue($"0x{v:X}");
}

internal static class Diag
{
    public delegate bool EnumProc(nint h, nint l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, nint l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint h, uint cmd);
    [DllImport("user32.dll")] public static extern int GetWindowLongW(nint h, int idx);
}
