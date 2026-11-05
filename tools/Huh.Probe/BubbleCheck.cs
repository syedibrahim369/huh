using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Huh.Core.Placement;
using CoreScreen = Huh.Core.Windowing.Screen;

/// <summary>
/// M2 verification: drives the real app + harness and checks the bubble against the spec.
///   focus never stolen, NOACTIVATE/TOOLWINDOW styles, placement == BubblePlacer, directly above its dialog in z-order,
///   follows moves, hides with a minimized owner, drops below unrelated windows, closes with the dialog.
/// Screenshots are cropped to the dialog + bubble only.
/// </summary>
internal static class BubbleCheck
{
    private sealed class Ctx(string harness, int huhPid, string outDir)
    {
        public string Harness = harness; public int HuhPid = huhPid; public string OutDir = outDir;
        public List<(string Name, bool Ok, string Detail)> Results = [];
        public void Check(string name, bool ok, string detail = "")
        {
            Results.Add((name, ok, detail));
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(detail.Length > 0 ? "  - " + detail : "")}");
        }
    }

    public static int Run(string harness, string appExe, string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var p in Process.GetProcessesByName("Huh")) { try { p.Kill(); p.WaitForExit(2000); } catch { } }
        // Readiness: the app logs "monitor started" once hooks + UIA are live.
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "huh", "logs", "huh.log");
        long logStart = File.Exists(log) ? new FileInfo(log).Length : 0;
        using var app = Process.Start(new ProcessStartInfo(appExe) { UseShellExecute = false })!;
        string? ready = null;
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 15000 && ready is null; Thread.Sleep(100))
            ready = ReadFrom(log, logStart).Split('\n').FirstOrDefault(l => l.Contains("monitor started"));
        Console.WriteLine(ready is null ? "app did not report ready within 15 s" : $"app ready: {ready.Trim()}");
        Thread.Sleep(300);
        var ctx = new Ctx(harness, app.Id, outDir);
        var monitors = CoreScreen.GetMonitors();
        var mon = monitors[0];
        Console.WriteLine($"monitor {mon.Bounds} work {mon.WorkArea} dpi {mon.Dpi} ({mon.Scale:P0})");

        try
        {
            Safe(ctx, () => Scenario(ctx, "mb-error-access-denied", positions: true, zorder: true));
            Safe(ctx, () => Scenario(ctx, "td-error-command-links"));
            Safe(ctx, () => Scenario(ctx, "mb-error-topmost"));
            Safe(ctx, () => Scenario(ctx, "shell-file-in-use"));
            Safe(ctx, () => Scenario(ctx, "mb-error-owned", minimize: true));
        }
        finally { try { app.Kill(); app.WaitForExit(3000); } catch { } } // never leave huh running (it inherits our stdout pipe)
        int fails = ctx.Results.Count(r => !r.Ok);
        Console.WriteLine($"\n{ctx.Results.Count - fails}/{ctx.Results.Count} checks passed. Screenshots: {outDir}");
        var md = new StringBuilder($"# M2 bubble checks ({DateTime.Now:yyyy-MM-dd HH:mm})\n\nDisplay: {mon.Bounds.Width}x{mon.Bounds.Height} at {mon.Scale:P0}\n\n**{ctx.Results.Count - fails}/{ctx.Results.Count} checks passed**\n\n| | Check | Detail |\n|---|---|---|\n");
        foreach (var r in ctx.Results) md.AppendLine($"| {(r.Ok ? "ok" : "**FAIL**")} | {r.Name} | {r.Detail} |");
        File.WriteAllText(Path.Combine(outDir, "m2-bubble.md"), md.ToString());
        return fails == 0 ? 0 : 1;
    }

    private static void Safe(Ctx ctx, Action run)
    {
        try { run(); }
        catch (Exception ex) { ctx.Check("scenario completed without exception", false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Scenario(Ctx ctx, string id, bool positions = false, bool zorder = false, bool minimize = false)
    {
        Console.WriteLine($"\n== {id}");
        using var harness = Process.Start(new ProcessStartInfo(ctx.Harness, $"run {id}") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            nint dialog = WaitFor(() => FindDialog(harness.Id), 8000);
            if (dialog == 0) { ctx.Check($"{id}: dialog appeared", false); return; }
            ForceForeground(dialog);
            Thread.Sleep(150);
            nint fgBefore = GetForegroundWindow();

            var sw = Stopwatch.StartNew();
            nint bubble = WaitFor(() => FindBubble(ctx.HuhPid), 4000);
            ctx.Check($"{id}: bubble shown", bubble != 0, bubble != 0 ? $"{sw.ElapsedMilliseconds} ms after focus" : "");
            if (bubble == 0) return;
            Thread.Sleep(600); // let layout settle (content-driven height)

            ctx.Check($"{id}: focus not stolen", GetForegroundWindow() == fgBefore && fgBefore == dialog,
                $"foreground={Describe(GetForegroundWindow())}");
            long ex = GetWindowLongW(bubble, -20);
            ctx.Check($"{id}: WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW", (ex & 0x08000000) != 0 && (ex & 0x80) != 0, $"exstyle=0x{ex:X}");
            CheckPlacement(ctx, id, "initial", dialog, bubble);
            ctx.Check($"{id}: directly above its dialog in z-order", ZIndex(bubble) < ZIndex(dialog) && ZIndex(dialog) - ZIndex(bubble) <= 2,
                $"z bubble={ZIndex(bubble)} dialog={ZIndex(dialog)}");
            bool dialogTopmost = (GetWindowLongW(dialog, -20) & 0x8) != 0;
            ctx.Check($"{id}: topmost matches dialog", ((GetWindowLongW(bubble, -20) & 0x8) != 0) == dialogTopmost, $"dialog topmost={dialogTopmost}");
            Shot(ctx, dialog, bubble, $"{id}-initial");

            if (positions)
            {
                var work = CoreScreen.GetMonitors()[0].WorkArea;
                var d = CoreScreen.FrameBounds(dialog)!.Value;
                (string name, int x, int y)[] spots =
                [
                    ("right-edge", work.Right - d.Width - 4, 300),
                    ("left-edge", work.Left + 4, 200),
                    ("near-bottom", 500, work.Bottom - d.Height - 4),
                    ("top-left-corner", work.Left, work.Top),
                    ("center", (work.Width - d.Width) / 2, (work.Height - d.Height) / 2),
                ];
                foreach (var (name, x, y) in spots)
                {
                    SetWindowPos(dialog, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE|NOZORDER|NOACTIVATE
                    Thread.Sleep(350);
                    CheckPlacement(ctx, id, name, dialog, bubble);
                    Shot(ctx, dialog, bubble, $"{id}-{name}");
                }

                // Simulated drag: many small moves, check the bubble keeps up at the end.
                for (int i = 0; i < 30; i++) { SetWindowPos(dialog, 0, 300 + i * 12, 250 + i * 4, 0, 0, 0x0015); Thread.Sleep(15); }
                Thread.Sleep(300);
                CheckPlacement(ctx, id, "after-drag", dialog, bubble);
            }

            if (zorder)
            {
                // An unrelated window covering the dialog must also cover the bubble (no floating over other apps).
                using var cover = Process.Start(new ProcessStartInfo(ctx.Harness, "run neg-plain-window") { UseShellExecute = false, CreateNoWindow = true })!;
                nint coverWnd = WaitFor(() => FindWindowOfPid(cover.Id, "HuhHarnessWindow"), 5000);
                if (coverWnd != 0)
                {
                    var d = CoreScreen.FrameBounds(dialog)!.Value;
                    SetWindowPos(coverWnd, 0, d.Left, d.Top, 1100, 600, 0x0004);
                    ForceForeground(coverWnd);
                    Thread.Sleep(400);
                    ctx.Check($"{id}: unrelated foreground window stays above bubble", ZIndex(coverWnd) < ZIndex(bubble),
                        $"z cover={ZIndex(coverWnd)} bubble={ZIndex(bubble)}");
                    ForceForeground(dialog);
                    Thread.Sleep(400);
                    ctx.Check($"{id}: bubble returns above dialog when dialog is refocused", ZIndex(bubble) < ZIndex(dialog) && ZIndex(bubble) < ZIndex(coverWnd),
                        $"z bubble={ZIndex(bubble)} dialog={ZIndex(dialog)} cover={ZIndex(coverWnd)}");
                }
                else ctx.Check($"{id}: cover window appeared", false);
                try { cover.Kill(); } catch { }
            }

            if (minimize)
            {
                nint owner = GetWindow(dialog, 4 /*GW_OWNER*/);
                if (owner != 0)
                {
                    ShowWindow(owner, 6 /*SW_MINIMIZE*/);
                    Thread.Sleep(500);
                    ctx.Check($"{id}: bubble hides when owner is minimized", !IsWindowVisible(bubble));
                    ShowWindow(owner, 9 /*SW_RESTORE*/);
                    ForceForeground(dialog);
                    Thread.Sleep(600);
                    ctx.Check($"{id}: bubble returns when owner is restored", IsWindowVisible(bubble));
                    if (IsWindowVisible(bubble)) CheckPlacement(ctx, id, "after-restore", dialog, bubble);
                }
                else ctx.Check($"{id}: dialog has an owner", false);
            }

            // Close the dialog: the bubble must go with it.
            PostMessageW(dialog, 0x0010, 0, 0);
            if (IsWindow(dialog)) { Thread.Sleep(200); if (IsWindow(dialog)) PostMessageW(dialog, 0x0111 /*WM_COMMAND*/, 2 /*IDCANCEL*/, 0); }
            Thread.Sleep(200);
            string how = "dialog closed";
            if (IsWindow(dialog)) // e.g. a TaskDialog without a Cancel button ignores WM_CLOSE: end the app instead (also a real-world path)
            {
                try { harness.Kill(entireProcessTree: true); } catch { }
                how = "app ended (dialog ignores WM_CLOSE)";
            }
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 2000 && IsWindow(bubble) && IsWindowVisible(bubble)) Thread.Sleep(20);
            ctx.Check($"{id}: bubble closes with dialog", !(IsWindow(bubble) && IsWindowVisible(bubble)), $"{how}, {t.ElapsedMilliseconds} ms");
        }
        finally
        {
            try { harness.Kill(entireProcessTree: true); harness.WaitForExit(2000); } catch { }
            Thread.Sleep(300);
        }
    }

    private static void CheckPlacement(Ctx ctx, string id, string label, nint dialog, nint bubble)
    {
        var d = CoreScreen.FrameBounds(dialog);
        var b = CoreScreen.FrameBounds(bubble);
        if (d is null || b is null) { ctx.Check($"{id}/{label}: rects readable", false); return; }
        var monitors = CoreScreen.GetMonitors();
        var target = BubblePlacer.MonitorFor(b.Value, monitors);
        double heightDip = b.Value.Height / target.Scale;
        var expected = BubblePlacer.Place(d.Value, Math.Round(b.Value.Width / target.Scale), heightDip, monitors);
        bool near = Math.Abs(expected.Rect.Left - b.Value.Left) <= 3 && Math.Abs(expected.Rect.Top - b.Value.Top) <= 3;
        bool inside = target.WorkArea.Contains(b.Value);
        bool noOverlap = expected.Side == BubbleSide.Overlay || d.Value.IntersectionArea(b.Value) == 0;
        ctx.Check($"{id}/{label}: placed {expected.Side}, inside work area, no overlap", near && inside && noOverlap,
            $"dialog {d.Value} bubble {b.Value} expected {expected.Rect}");
    }

    // ------------------------------------------------------------ screenshots (cropped to dialog + bubble)
    private static void Shot(Ctx ctx, nint dialog, nint bubble, string name)
    {
        var dq = CoreScreen.FrameBounds(dialog); var bq = CoreScreen.FrameBounds(bubble);
        if (dq is null || bq is null) return; // a window vanished: nothing to capture
        var d = dq.Value; var b = bq.Value;
        const int pad = 24;
        int l = Math.Min(d.Left, b.Left) - pad, t = Math.Min(d.Top, b.Top) - pad;
        int r = Math.Max(d.Right, b.Right) + pad, btm = Math.Max(d.Bottom, b.Bottom) + pad;
        var bounds = CoreScreen.GetMonitors()[0].Bounds;
        l = Math.Max(l, bounds.Left); t = Math.Max(t, bounds.Top); r = Math.Min(r, bounds.Right); btm = Math.Min(btm, bounds.Bottom);
        using var bmp = new Bitmap(r - l, btm - t);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(l, t, 0, 0, bmp.Size);
        bmp.Save(Path.Combine(ctx.OutDir, name + ".png"), ImageFormat.Png);
    }

    /// <summary>
    /// Waits for huh (already running) to show its next bubble, then saves a screenshot cropped to that bubble and its dialog.
    /// The dialog rectangle comes from huh's log ("anchor=l,t,r,b"), so embedded dialogs (browser alerts) crop correctly too.
    /// </summary>
    public static int Snap(string outPath, int timeoutMs, int settleMs)
    {
        string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "huh", "logs", "huh.log");
        long start = File.Exists(log) ? new FileInfo(log).Length : 0;
        var huh = Process.GetProcessesByName("Huh").FirstOrDefault();
        if (huh is null) { Console.Error.WriteLine("huh is not running"); return 2; }

        // huh logs:  "bubble 0xB: show beside dialog 0xD (...)"  then  "bubble 0xB: anchor=l,t,r,b"
        string? showLine = null, anchorLine = null;
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < timeoutMs && anchorLine is null; Thread.Sleep(100))
        {
            var lines = ReadFrom(log, start).Split('\n');
            anchorLine = lines.FirstOrDefault(l => l.Contains(": anchor="));
            showLine = lines.FirstOrDefault(l => l.Contains("show beside dialog 0x"));
        }
        if (anchorLine is null || showLine is null) { Console.Error.WriteLine("no bubble within timeout"); return 1; }

        var nums = anchorLine[(anchorLine.IndexOf("anchor=", StringComparison.Ordinal) + 7)..].Trim().Split(',').Select(int.Parse).ToArray();
        var anchor = new PxRect(nums[0], nums[1], nums[2], nums[3]);
        nint bubble = HexAfter(anchorLine, "bubble 0x");
        nint dialog = HexAfter(showLine, "dialog 0x");

        // Make sure what we capture is really the dialog + bubble (a background-launched dialog can open BEHIND other windows,
        // and then the crop would capture the user's screen instead).
        ForceForeground(dialog);
        Thread.Sleep(settleMs);
        var b = CoreScreen.FrameBounds(bubble);
        if (b is null) { Console.Error.WriteLine("bubble window not found"); return 1; }
        if (!TopmostAt(anchor, dialog) || !TopmostAt(b.Value, bubble))
        {
            Console.Error.WriteLine("dialog or bubble is covered by another window; NOT saving a screenshot");
            return 1;
        }

        // Capture the union, then blank every pixel that is not the dialog or the bubble: nothing else on screen can leak.
        var bounds = CoreScreen.GetMonitors()[0].Bounds;
        int l = Math.Max(Math.Min(anchor.Left, b.Value.Left), bounds.Left), t = Math.Max(Math.Min(anchor.Top, b.Value.Top), bounds.Top);
        int r = Math.Min(Math.Max(anchor.Right, b.Value.Right), bounds.Right), btm = Math.Min(Math.Max(anchor.Bottom, b.Value.Bottom), bounds.Bottom);
        using var shot = new Bitmap(r - l, btm - t);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(l, t, 0, 0, shot.Size);

        const int pad = 16;
        using var outBmp = new Bitmap(shot.Width + 2 * pad, shot.Height + 2 * pad);
        using (var g = Graphics.FromImage(outBmp))
        {
            g.Clear(Color.FromArgb(0x5A, 0x5F, 0x66)); // neutral backdrop instead of the real desktop
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            foreach (var rect in new[] { anchor, b.Value })
            {
                // Inset 1 px and round the corners (Windows 11: 8 DIP) so no desktop pixels survive at edges/corners.
                var src = new Rectangle(rect.Left - l + 1, rect.Top - t + 1, rect.Width - 2, rect.Height - 2);
                var dst = new Rectangle(src.X + pad, src.Y + pad, src.Width, src.Height);
                using var path = RoundedRect(dst, 10);
                g.SetClip(path);
                g.DrawImage(shot, dst, src, GraphicsUnit.Pixel);
                g.ResetClip();
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        outBmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine($"saved {outPath} ({outBmp.Width}x{outBmp.Height}) anchor {anchor} bubble {b.Value}");
        return 0;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static nint HexAfter(string s, string marker)
    {
        int i = s.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        int j = i;
        while (j < s.Length && Uri.IsHexDigit(s[j])) j++;
        return (nint)Convert.ToInt64(s[i..j], 16);
    }

    /// <summary>Is <paramref name="expected"/> (or a window inside it) the topmost window at the center and all four corners (inset)?</summary>
    private static bool TopmostAt(PxRect r, nint expected)
    {
        int inset = 12;
        (int X, int Y)[] pts = [r.Center, (r.Left + inset, r.Top + inset), (r.Right - inset, r.Top + inset), (r.Left + inset, r.Bottom - inset), (r.Right - inset, r.Bottom - inset)];
        foreach (var (x, y) in pts)
        {
            var h = WindowFromPoint(new POINT { X = x, Y = y });
            if (h != expected && GetAncestor(h, 2 /*GA_ROOT*/) != expected)
            {
                var root = GetAncestor(h, 2);
                Console.Error.WriteLine($"  covered at ({x},{y}) by {Describe(root)}, expected 0x{expected:X} {ClassOf(expected)}");
                return false;
            }
        }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint h, uint flags);

    private static string ReadFrom(string path, long offset)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= offset) return "";
            fs.Seek(offset, SeekOrigin.Begin);
            return new StreamReader(fs).ReadToEnd();
        }
        catch (IOException) { return ""; }
    }

    // ------------------------------------------------------------ window helpers
    private static nint WaitFor(Func<nint> probe, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { var h = probe(); if (h != 0) return h; Thread.Sleep(30); }
        return 0;
    }

    private static nint FindDialog(int harnessPid)
    {
        var pids = new HashSet<int> { harnessPid };
        nint found = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains((int)pid) && IsWindowVisible(h) && ClassOf(h) is "#32770" or "OperationStatusWindow") { found = h; return false; }
            return true;
        }, 0);
        return found;
    }

    private static nint FindBubble(int huhPid) => FindWindowOfPid(huhPid, "WinUIDesktopWin32WindowClass");

    private static nint FindWindowOfPid(int pid, string cls)
    {
        nint found = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && IsWindowVisible(h) && ClassOf(h) == cls) { found = h; return false; }
            return true;
        }, 0);
        return found;
    }

    /// <summary>Position in the top-level z-order (0 = topmost).</summary>
    private static int ZIndex(nint target)
    {
        int i = 0, result = -1;
        EnumWindows((h, _) => { if (h == target) { result = i; return false; } if (IsWindowVisible(h)) i++; return true; }, 0);
        return result;
    }

    /// <summary>Test-only: bypass the foreground lock (a background process may not normally take focus).</summary>
    private static void ForceForeground(nint hwnd)
    {
        keybd_event(0x12 /*VK_MENU*/, 0, 0, 0);
        keybd_event(0x12, 0, 2 /*KEYUP*/, 0);
        SetForegroundWindow(hwnd);
    }

    private static string ClassOf(nint h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    private static string Describe(nint h) { GetWindowThreadProcessId(h, out var pid); return $"0x{h:X} {ClassOf(h)} pid={pid}"; }

    private delegate bool EnumProc(nint h, nint l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, nint l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint h);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint h, int idx);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint h, int cmd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint h, uint cmd);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint h, uint msg, nint w, nint l);
}
