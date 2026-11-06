using System.Net;
using System.Text.Json.Nodes;
using Huh.Core.AI;
using Huh.Core.Decoding;
using Huh.Core.Dialogs;
using Huh.Core.Facts;
using Huh.Core.Privacy;
using Huh.Core.Provenance;
using Huh.Core.Settings;
using Microsoft.Win32;

namespace Huh.Tests;

public class SettingsTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "huh-tests", Guid.NewGuid().ToString("N"), "settings.json");

    private static DialogFacts Facts(string exe = "contoso.exe", SourceKind kind = SourceKind.App, string? onBehalfOf = null, string? browser = null)
    {
        var path = $@"C:\Program Files\Contoso\{exe}";
        var d = new DialogInfo
        {
            Hwnd = 1, Title = "Contoso", Message = "Access is denied. (0x80070005)", Buttons = ["OK"], Severity = DialogSeverity.Error,
            Process = new ProcessDetails(42, Path.GetFileNameWithoutExtension(exe), path, ElevationState.NotElevated),
        };
        var src = new SourceInfo(kind, "Contoso Tool", "Contoso", true, new SignatureInfo(SignatureState.Signed, "Contoso"), exe, path,
            OnBehalfOf: onBehalfOf, Browser: browser);
        return new DialogFacts(d, ErrorCodeDecoder.Decode(d.Message), src, FactsBuilder.Headline(d));
    }

    // ================================================================ defaults + persistence

    [Fact]
    public void Defaults_are_private_and_quiet()
    {
        var s = HuhSettings.Load(TempFile()); // missing file
        Assert.False(s.AiEnabled);
        Assert.False(s.AiConsentDecided);
        Assert.False(s.WelcomeSeen);
        Assert.Empty(s.ExcludedApps);
        Assert.Equal(["gemini-3.8-flash", "gemini-3.5-flash-lite"], s.Models);
    }

    [Fact]
    public void Round_trip_keeps_everything()
    {
        var path = TempFile();
        var s = new HuhSettings { WelcomeSeen = true, AiConsentDecided = true, AiEnabled = true, AiConsentAt = DateTimeOffset.Parse("2026-11-06T10:05:00+00:00"), Model = "gemini-x-1" };
        s.Exclude("Notepad.EXE");
        s.NoteSeen("msiexec.exe", "Windows Installer", DateTimeOffset.Parse("2026-11-06T12:00:00+00:00"));
        s.Save(path);

        var t = HuhSettings.Load(path);
        Assert.True(t.WelcomeSeen && t.AiConsentDecided && t.AiEnabled);
        Assert.Equal(s.AiConsentAt, t.AiConsentAt);
        Assert.Equal("gemini-x-1", t.Model);
        Assert.Equal(["notepad.exe"], t.ExcludedApps);
        Assert.Equal("Windows Installer", Assert.Single(t.RecentApps).Name);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_with_ai_off()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"aiEnabled\": true, oops");
        Assert.False(HuhSettings.Load(path).AiEnabled);
    }

    [Fact]
    public void Answered_consent_before_welcome_existed_counts_as_welcome_seen()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "aiConsentDecided": true, "aiEnabled": false }""");
        Assert.True(HuhSettings.Load(path).WelcomeSeen);
    }

    // ================================================================ model names (they go into the request URL)

    [Theory]
    [InlineData("gemini-3.8-flash", true)]
    [InlineData("gemini-3.5-flash-lite", true)]
    [InlineData("gemini-2.5-pro-preview-06-05", true)]
    [InlineData("", false)]
    [InlineData("Gemini-3", false)]
    [InlineData("../../v1/files", false)]
    [InlineData("gemini-3.8-flash:generateContent?key=x", false)]
    [InlineData("gemini 3", false)]
    [InlineData("gemini-3.8-flash/../../x", false)]
    [InlineData("-flash", false)]
    public void Model_names_are_plain_ids(string name, bool ok) => Assert.Equal(ok, HuhSettings.IsValidModelName(name));

    [Fact]
    public void Invalid_model_in_file_is_replaced_by_the_default()
    {
        var path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "model": "../evil?x=", "fallbackModel": "a b" }""");
        var s = HuhSettings.Load(path);
        Assert.Equal(HuhSettings.DefaultModel, s.Model);
        Assert.Equal(HuhSettings.DefaultFallbackModel, s.FallbackModel);
    }

    [Fact]
    public async Task Explainer_reads_models_live_and_skips_invalid_names()
    {
        var models = new List<string> { "bad name", "gemini-live-1" };
        var fake = new FakeGemini().Then(HttpStatusCode.OK, "ok-access-denied.json").Then(HttpStatusCode.OK, "ok-access-denied.json");
        var g = new GeminiExplainer(new HttpClient(fake), () => "AQ.test-key-0000000000000000000000000000000000000000", () => models);
        await g.ExplainAsync(new ExplainRequest("x", "k"), CancellationToken.None);
        Assert.Contains("/models/gemini-live-1:generateContent", Assert.Single(fake.Requests).Url);

        models[1] = "gemini-live-2"; // changed in Settings
        await g.ExplainAsync(new ExplainRequest("x", "k"), CancellationToken.None);
        Assert.Contains("/models/gemini-live-2:generateContent", fake.Requests[1].Url);
    }

    [Fact]
    public async Task No_valid_model_means_no_request()
    {
        var fake = new FakeGemini();
        var g = new GeminiExplainer(new HttpClient(fake), () => "AQ.test-key-0000000000000000000000000000000000000000", () => ["../x"]);
        var o = await g.ExplainAsync(new ExplainRequest("x", "k"), CancellationToken.None);
        Assert.Equal(UnavailableReason.ModelUnavailable, o.Reason);
        Assert.Empty(fake.Requests);
    }

    // ================================================================ exclusions + recently seen apps

    [Fact]
    public void Exclusions_match_exe_names_case_insensitively()
    {
        var s = new HuhSettings();
        Assert.True(s.Exclude(@"C:\Windows\System32\NOTEPAD.EXE"));
        Assert.False(s.Exclude("notepad.exe")); // already there
        Assert.True(s.IsExcluded("Notepad.exe"));
        Assert.False(s.IsExcluded("notepad2.exe"));
        Assert.False(s.IsExcluded(null));
        Assert.True(s.Include("NOTEPAD.exe"));
        Assert.Empty(s.ExcludedApps);
    }

    [Fact]
    public void App_key_is_the_program_a_windows_hard_error_is_about()
    {
        Assert.Equal("game.exe", HuhSettings.AppKeyFor(Facts("csrss.exe", SourceKind.Windows, onBehalfOf: "Game.exe")));
        Assert.Equal("Game", HuhSettings.AppNameFor(Facts("csrss.exe", SourceKind.Windows, onBehalfOf: "Game.exe")));
        Assert.Equal("contoso.exe", HuhSettings.AppKeyFor(Facts("Contoso.exe")));
        // A web page's alert belongs to the browser.
        Assert.Equal("msedge.exe", HuhSettings.AppKeyFor(Facts("msedge.exe", SourceKind.WebPage, browser: "Microsoft Edge")));
        Assert.Equal("Microsoft Edge", HuhSettings.AppNameFor(Facts("msedge.exe", SourceKind.WebPage, browser: "Microsoft Edge")));
    }

    [Fact]
    public void Recent_apps_are_most_recent_first_unique_and_capped()
    {
        var s = new HuhSettings();
        var t0 = DateTimeOffset.Parse("2026-11-06T00:00:00Z");
        for (int i = 0; i < 20; i++) s.NoteSeen($"app{i}.exe", $"App {i}", t0.AddMinutes(i));
        s.NoteSeen("APP3.EXE", "App 3", t0.AddHours(1));
        Assert.Equal(HuhSettings.MaxRecentApps, s.RecentApps.Count);
        Assert.Equal("app3.exe", s.RecentApps[0].Exe);
        Assert.Single(s.RecentApps, a => a.Exe == "app3.exe");
    }

    // ================================================================ start with Windows (test keys under HKCU\Software\huh-tests)

    [Fact]
    public void Startup_registration_round_trip_and_task_manager_override()
    {
        var root = $@"Software\huh-tests\{Guid.NewGuid():N}";
        var reg = new StartupRegistration(root + @"\Run", root + @"\Approved");
        try
        {
            Assert.False(reg.IsEnabled()); // off by default
            reg.Enable(@"C:\Apps\huh\Huh.exe");
            Assert.True(reg.IsEnabled());
            Assert.Equal("\"C:\\Apps\\huh\\Huh.exe\"", reg.RegisteredCommand());

            // Disabled in Task Manager -> reported as off; enabling here clears that override.
            using (var k = Registry.CurrentUser.CreateSubKey(root + @"\Approved"))
                k.SetValue(StartupRegistration.ValueName, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.False(reg.IsEnabled());
            reg.Enable(@"C:\Apps\huh\Huh.exe");
            Assert.True(reg.IsEnabled());

            reg.Disable();
            Assert.False(reg.IsEnabled());
            Assert.Null(reg.RegisteredCommand());
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }

    [Theory]
    [InlineData("Huh.exe")]
    [InlineData(@"C:\Apps\huh\Huh.exe --evil")]
    [InlineData(@"C:\Apps\huh\script.cmd")]
    public void Startup_needs_a_full_exe_path(string path)
    {
        var reg = new StartupRegistration(@"Software\huh-tests\never\Run", @"Software\huh-tests\never\Approved");
        Assert.Throws<ArgumentException>(() => reg.Enable(path));
    }

    [Fact]
    public void Startup_repair_only_when_the_registered_exe_is_gone()
    {
        var root = $@"Software\huh-tests\{Guid.NewGuid():N}";
        var reg = new StartupRegistration(root + @"\Run", root + @"\Approved");
        var existing = Environment.ProcessPath!;
        try
        {
            reg.Enable(@"C:\definitely\not\here\Huh.exe");
            Assert.True(reg.RepairPath(existing));
            Assert.Equal($"\"{existing}\"", reg.RegisteredCommand());

            reg.Enable(existing); // another copy that still exists is left alone
            Assert.False(reg.RepairPath(@"C:\Other\Huh.exe"));
            Assert.Equal($"\"{existing}\"", reg.RegisteredCommand());

            reg.Disable(); // off stays off
            Assert.False(reg.RepairPath(existing));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
    }

    // ================================================================ privacy preview

    [Fact]
    public async Task Privacy_preview_body_is_byte_for_byte_what_is_sent()
    {
        var r = new Redactor(@"C:\Users\alice", "alice", "ALICE-LAPTOP");
        var facts = SampleFacts.Create(@"C:\Users\alice");
        var preview = RequestPreview.For(facts, r, "gemini-3.8-flash");

        var fake = new FakeGemini().Then(HttpStatusCode.OK, "ok-access-denied.json");
        var g = new GeminiExplainer(new HttpClient(fake), () => "AQ.test-key-0000000000000000000000000000000000000000", ["gemini-3.8-flash"]);
        await g.ExplainAsync(Prompt.Build(facts, r), CancellationToken.None);
        var sent = Assert.Single(fake.Requests);

        Assert.Equal(sent.Url, preview.Url);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sent.Body), JsonNode.Parse(preview.Body)));
        // Redaction is visible in the preview, and the key never is.
        Assert.DoesNotContain("alice", preview.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", preview.Body);
        Assert.DoesNotContain("AQ.", string.Join(" ", preview.Headers.Select(h => h.Value)));
        Assert.Equal(RequestPreview.KeyPlaceholder, preview.Headers.Single(h => h.Name == "x-goog-api-key").Value);
    }

    [Fact]
    public void Sample_dialog_decodes_and_contains_the_profile_path()
    {
        var f = SampleFacts.Create(@"C:\Users\bob");
        Assert.Contains(@"C:\Users\bob\Documents", f.Dialog.Message);
        Assert.Equal("0x80070005", Assert.Single(f.Codes).Display);
    }

    // ================================================================ cache housekeeping

    [Fact]
    public void Cache_count_and_clear()
    {
        var dir = Path.Combine(Path.GetTempPath(), "huh-tests", Guid.NewGuid().ToString("N"));
        var c = new ExplanationCache(dir);
        Assert.Equal(0, c.Count);
        c.Put("a", Explanation.Sanitized("Cause.", ["Step."], "none", "", 0.5), "m");
        c.Put("b", Explanation.Sanitized("Cause.", ["Step."], "none", "", 0.5), "m");
        Assert.Equal(2, c.Count);
        Assert.Equal(2, c.Clear());
        Assert.Equal(0, c.Count);
        Assert.Null(c.TryGet("a", out _));
    }
}
