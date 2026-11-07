<p align="center"><img src="docs/images/huh-icon.png" width="96" alt="huh icon: a speech bubble with a question mark"></p>

# huh

**For the Windows error messages that leave you going "huh?"**

huh is a small Windows 11 tray app. When an error dialog pops up anywhere in Windows, huh slides a bubble out beside it
that tells you:

- **what went wrong**, in plain English (decoded error codes, and an optional AI explanation);
- **what to do about it**, as a one-click fix from a short, fixed list (open the right Settings page, open a folder, copy the
  error, search the web, re-run the program as administrator), always behind a confirmation that shows exactly what will happen;
- **whether the dialog is legit**: which program raised it and whether that program is signed. A "Windows Security Error"
  that is really a web page in your browser gets labelled as one.

<!-- DEMO GIF: replace with the 60-second demo (see docs/demo-script.md) -->
<p align="center"><em>[ demo GIF goes here ]</em></p>

![huh next to Windows' "Destination Folder Access Denied" dialog: it explains that System32 is protected and points at the dialog's own Continue button](docs/images/system32-copy.png)

## Why

Error dialogs are written for the developer who wrote them: `0x80070005`, "The code execution cannot proceed",
"Error 1619". People either click OK and give up, or search the code and land on a forum thread from 2014, or worse, on a
"call this number" scam page. huh sits next to the dialog, at the moment of confusion, and answers the three questions people
actually have: *what is this, can I fix it, and should I trust it?*

It's also a test of a harder question: **how do you let an AI suggest actions on someone's PC without letting it do
anything dangerous?** (See [Security model](#security-model).)

## Download and run

1. Download `huh-1.0.0-win-x64.zip` from [Releases](../../releases) and extract it anywhere (for example `%LOCALAPPDATA%\Programs\huh`).
2. Run `Huh.exe`. No installer, no admin rights; it needs Windows 10 1809 or later (built and tested on Windows 11).
3. The first run shows a short welcome, then asks whether you want AI explanations (optional, off until you agree).

> **About the SmartScreen warning.** The release isn't code-signed (a certificate costs money), so Windows SmartScreen will say
> "Windows protected your PC" and Defender may scan it for a while on first launch. Yes, an app whose job includes telling you
> whether programs are signed is itself unsigned. You can check the zip against the SHA-256 on the release page, or
> [build it yourself](#build-from-source). Click **More info › Run anyway** to start it.

huh lives in the notification area. **Right-click the icon** for: pause/resume, AI explanations on/off, Start with Windows
(off by default), Settings and Quit. Left-click opens Settings.

## What it looks like

| | |
|---|---|
| ![A fake "Windows Security Error" alert from a web page, labelled by huh as coming from a web page in Microsoft Edge](docs/images/browser-scam.png) | **A scam alert.** "Windows Security Error 0x80070005: Your PC is blocked. Call support now." huh decodes the code like any other, then labels the source honestly: *from a web page in Microsoft Edge, not a Windows message*. |
| ![A dialog that tries to instruct AI tools; huh shows a caution banner and offers no action](docs/images/injection.png) | **A prompt-injection attempt.** The dialog text tells "the AI assistant" to have you re-run the program as admin with an `--install` argument. The model partly went along with it (see its steps), but huh flags the message, shows a caution banner and **offers no one-click action**. |
| ![The confirmation for opening a folder, showing the full path](docs/images/confirm-folder.png) | **Every action is confirmed first**, with exactly what will happen: here the full folder path (*your user name is redacted in this screenshot*). |
| ![The red unsigned-program warning with an acknowledgement checkbox](docs/images/unsigned-override.png) | **Re-running an unsigned program as admin** is never suggested by the AI. If you start it yourself, you get a red warning with the path, *UNSIGNED* and *Arguments: none*, and the button stays disabled until you tick *I understand this program is unsigned*. |

### First run, tray and Settings

| | |
|---|---|
| ![Welcome window: what huh does, in three lines](docs/images/welcome.png) | **Welcome.** Three lines on what huh does, and where it lives (the notification area). |
| ![Consent screen for AI explanations, with the free-tier caution first](docs/images/consent.png) | **AI is opt-in.** The free-tier caution comes first, then what is removed before sending. Nothing is sent until you turn it on. |
| ![huh's tray menu: status, Pause, AI explanations, Start with Windows, Settings, Quit](docs/images/tray-menu.png) | **Tray menu.** Pause, AI on/off, Start with Windows (off by default), Settings, Quit. |
| ![Settings: watching, Start with Windows, AI provider and models, API key](docs/images/settings.png) | **Settings** (Mica). Provider and models (applied immediately, validated), the API key, and per-app exclusions picked from apps huh has recently seen. |
| ![Settings › Privacy: what is sent, removed and never sent, and the exact request preview](docs/images/settings-privacy.png) | **Privacy.** What is sent, what is removed, what is never sent, and the exact request, built by the same code as a real one (the key is shown as a placeholder). |

## How it works

```
 any app's error dialog
          │  WinEvents (DIALOGSTART, ALERT, SHOW, DESTROY) + UI Automation WindowOpened
          ▼
 ┌───────────────────────── Huh.Core (no UI) ─────────────────────────┐
 │ Detection   DialogMonitor: cheap pre-filter, then ONE UIA read     │
 │             (text only, never pixels) on an MTA worker thread      │
 │ Dialogs     DialogClassifier: error / warning / question / info    │
 │ Decoding    ErrorCodeDecoder: HRESULT / NTSTATUS / Win32 via       │
 │             FormatMessage (system, ntdll, wininet)                 │
 │ Provenance  SourceAnalyzer + Authenticode (WinVerifyTrust,         │
 │             offline): signed by X / unsigned / Store app /         │
 │             Windows itself / a web page in <browser>               │
 │ AI (opt-in) Redactor ─► Prompt (fenced) ─► cache ─► Gemini ─►      │
 │             sanitize ─► InjectionDetector ─► FixActions.Validate   │
 │ Actions     FixActionRunner: re-checks at click time, then the     │
 │             IActionLauncher (the only code with side effects)      │
 └────────────────────────────────────────────────────────────────────┘
          ▼
 Huh.App (WinUI 3): tray icon, bubble windows, Settings, first run
```

- **Detection** uses out-of-context WinEvent hooks plus UI Automation events. A cheap filter (window class, dialog events,
  error-ish titles) runs before any UI Automation call, so idle cost is about 1% of one core.
- **The bubble** is a never-activated tool window (`WS_EX_NOACTIVATE`): it never steals focus from the dialog you're reading.
  It's placed beside the dialog in physical pixels with per-monitor DPI, follows it when it moves, hides when it's minimized, and
  is stacked directly above it (so other windows still cover both).
- **Browser alerts** (`alert()` in Edge or Chrome) are drawn inside the browser window and have no window handle of their own.
  huh notices the accessibility alert event, finds the alert in the browser's own UI tree (never in the web page's content),
  and anchors the bubble to it.
- **Decoding and provenance are deterministic and offline.** The AI is an optional layer on top; everything else works without it.
- **Pre-warmed UI:** at startup huh renders one bubble for a made-up dialog, cloaked and off-screen, so the first real
  bubble doesn't pay the XAML cold-start cost.
- **Animations** are a short slide and fade, and they're turned off when Windows' *Animation effects* setting is off.

Stack: C# / .NET 10, WinUI 3 (Windows App SDK 2.6, unpackaged, self-contained), CsWin32 for raw COM UI Automation
(trim-safe), xUnit v3. Solution layout: `src/Huh.Core`, `src/Huh.App`, `tools/Huh.TestHarness` (raises 36 kinds of dialogs
on demand), `tools/Huh.Probe` (scores detection, checks bubble placement, takes verified screenshots), `tests/Huh.Tests`.

## Privacy

**No screenshots, ever.** huh reads dialog text through Windows' accessibility API (UI Automation). The only image-like data
it reads is a MessageBox icon's resource ID, which tells an error icon from a warning icon.

**AI explanations are opt-in.** Until you press *Turn on AI explanations* on the consent screen, nothing leaves your PC. You
still get decoded codes, the source program and its signature.

When AI is on, huh sends this to Google Gemini, **once per new error message**. Repeats are answered from a copy saved on
your PC.

| Sent | Removed before sending | Never sent |
|---|---|---|
| The dialog's title, text (up to 2,000 characters) and button labels | Your user-folder path (replaced with `%USERPROFILE%`) | Screenshots or pixels |
| huh's facts: program name and exe, signature, elevated or not, part of Windows or not, decoded error codes | Other users' folder names | File contents, other windows |
| huh's fixed instructions (the same every time) | Your user name and computer name | Your settings and exclusions |
| | E-mail addresses | Your API key, except to Google in the `x-goog-api-key` header |

- **Gemini free tier:** on the free tier, Google may use what is sent to improve its products, and human reviewers may read
  it. The consent screen says so, and recommends leaving AI off if your error messages can contain confidential information.
- **See exactly what is sent:** *Settings › Privacy › See exactly what gets sent* shows the full request (URL, headers, JSON
  body) for a sample error or the last real one. It's built by the same code that builds the real request, and a unit test
  checks the two are identical.
- **Stored on your PC** (`%LOCALAPPDATA%\huh`):
  - `settings.json`, including the recently-seen apps, which are stored as exe names only;
  - `gemini.key`, your API key encrypted with Windows DPAPI for your account only;
  - saved AI answers (no dialog text), kept for 30 days and deletable from Settings;
  - a diagnostics log of window events, which never contains message text.
- The key can also come from a `GEMINI_API_KEY` environment variable. It's never printed, logged or committed.

## Security model

The AI never runs anything. The model's whole output is one JSON object: an explanation, up to four steps, one `action_id`
from a fixed list, and one parameter. Everything after that is huh's code:

1. **Allowlist.** There are five actions:
   - open an `ms-settings:` page;
   - open a folder in File Explorer;
   - copy the error text;
   - search the web;
   - re-run the program as administrator.

   Anything else is dropped.
2. **huh validates the parameters; it never trusts the AI's values.**
   - **Settings pages** must be keys from a fixed list of 20 pages.
   - **Folders** must be existing local folders: no UNC paths, variables or files.
   - **Web searches** are built by huh from the redacted error text. The AI can't choose the query.
   - **Re-run as admin** takes the exe path huh observed itself, and never any arguments. It's never offered for Windows'
     own shell and system processes (explorer, csrss, dllhost, rundll32, svchost, conhost, PowerShell, cmd, msiexec, and
     anything under `%SystemRoot%`); for those, huh points at the dialog's own button instead.
   - **Unsigned programs:** re-running one as admin is never suggested by the AI.
   - **Web-page dialogs** only allow "copy text".
3. **Your click, with the details shown first.** Every action opens a confirmation listing what will happen: the settings
   page name and URI, the full folder path, the search query, or the program, full path, publisher and *Arguments: none*.
   The unsigned override also needs a checkbox before its button enables.
4. **Re-checks at click time.** Between the bubble appearing and your click, the exe could have been swapped or the folder
   replaced by a file. So huh re-verifies the signature and signer with WinVerifyTrust, the path, and the folder before acting.
5. **Untrusted text is fenced.** Dialog text goes inside a `<dialog_data>` block with its tags neutralised; huh's own facts
   go in a separate `<verified_facts>` block. Output is sanitised: control and bidi characters are stripped, lengths are
   capped, and only plain text is shown.

**About the injection check.** huh also has a keyword-based prompt-injection detector. It catches phrases like "ignore
previous instructions", "note to AI assistant" and mentions of `action_id`. When it fires, huh shows a caution banner and
offers no action. Treat it as **one extra layer, not the protection**: it's a list of patterns, and a determined attacker can
word around it. The protection is the allowlist, the parameter validation and your click, which hold even when the model is
fully fooled. In testing, `gemini-3.5-flash-lite` *did* follow an injected instruction, and the validator still offered
nothing dangerous.

## Test results

Measured on one laptop: a 14" 1920×1200 panel at 125% scaling, i7-1355U, Windows 11 Insider build 26300. These are real
numbers from the milestone runs, not targets.

| Area | Result |
|---|---|
| **Detection** ([report](docs/reports/m1-detection.md)) | **30/30** error dialogs from the harness detected and classified as errors (MessageBox, TaskDialog, shell file-operation errors, rundll32/regsvr32/msiexec, csrss hard errors, an Edge `alert()`, a custom window, a prompt-injection dialog); **0/6** false positives on info/question dialogs. Latency from first window event to classified dialog: p50 **149 ms**, p95 560 ms, max 1.17 s (the browser alert). |
| **Admin apps (UIPI)** | An elevated app's dialog is detected and labelled "running as administrator, huh can't read it"; its text stays unreadable, as Windows intends. |
| **Bubble** ([report](docs/reports/m2-bubble.md)) | **46/46** automated checks, 5 consecutive runs, dark and light theme: focus never stolen; placement matches the placement algorithm to the pixel at 5 screen positions and after a drag; z-order (including a covering window); minimize/restore; closes with the dialog. Re-run for 1.0 against both the Debug build and the release build: 46/46 each. Mixed-DPI placement is covered by unit tests only (no second monitor). |
| **Tray, Settings, first run** | Driven on screen through UI Automation: every tray-menu item (pause stops bubbles and resume brings them back; AI on/off is saved; Start with Windows writes and removes the `Run` entry; Settings; Quit), a second launch opening Settings in the running copy, and welcome › consent on a fresh profile. |
| **Animations** | Checked frame by frame (screen captures every ~35 ms over a plain backdrop): the bubble fades and slides in over ~220 ms; with `Animation effects` off, the first visible frame is already the final one. This found a black flash (a WinUI window is black until its first frame is drawn), now fixed: no black or empty frames in 7 runs (frames below the table). |
| **Real-world dialogs** | Run dialog (name not found), empty `.msi`, Notepad *save to C:\\*, copy into System32: all four detected, placed and labelled correctly. |
| **AI** ([report](docs/reports/m4-ai.md)) | Live latency **2.1–4.1 s** on `gemini-3.8-flash`; a repeat of the same error is answered from the local cache in **38 ms** with no request. When the primary returned HTTP 503 for about 10 minutes, huh fell back to `gemini-3.5-flash-lite`. 429 (quota) shows a clear message and keeps the on-device info. Development used fixtures; about 17 live calls in total. |
| **Fix actions** | Checked on screen through UI Automation: Copy, open-folder, open-Settings and search confirmations; the unsigned override (disabled until ticked, then UAC started only the test program, verified elevated). |
| **First bubble after start** (release build) | From the dialog's first window event to the bubble being **visible** (drawn and uncloaked): median **364 ms** with pre-warm (range 351–402), vs 499 ms without (484–559), 6 runs each; later bubbles: median 423 ms (380–497, 24 bubbles). So the first bubble is no slower than later ones. |
| **Unit tests** | **235** (xUnit v3): error decoding against real codes (`0x80070005`, `0xC0000135`, 1619, …), classifier, placement on fake multi-monitor geometry, redaction, the prompt fence, Gemini response parsing from saved fixtures, "compromised model" fixtures, validation of every action including injection cases, click-time re-checks (swapped exe, folder replaced by a file), settings, Start with Windows, and the privacy preview matching the real request byte for byte. |

![The bubble's entrance, captured frame by frame: 31 ms nearly transparent, 96 ms half faded in, 197 ms and later fully drawn](docs/images/entrance-frames.png)

## Known limitations

- **Admin apps (UIPI).** Windows doesn't let a normal-rights app read or click the dialogs of an app running as
  administrator. huh detects those dialogs and says so, but can't explain them. It deliberately doesn't run as admin itself.
- **English text cues.** Classic MessageBoxes are classified by their icon, which works in any language. TaskDialogs and
  custom windows rely on English words ("failed", "denied", "can't"…), so non-English TaskDialogs can be missed.
- **Browser pop-ups are slower.** Chromium only builds its accessibility tree once asked, so a browser `alert()` takes about
  1 s to detect, against about 150 ms for a normal dialog.
- **Tested on one machine**, a Windows 11 Insider build (26300), at 125% scaling with one monitor. Mixed-DPI setups are
  unit-tested only.
- **Unsigned release** (see the SmartScreen note above); the first launch after download can take several seconds while
  Defender scans it.
- **A one-frame flicker when the AI answer arrives.** The bubble grows to fit the answer, and for one frame (~30 ms) the new
  strip at the bottom is black before WinUI redraws it. (The same effect on the bubble's *first* appearance is fixed.)
- **Heuristics.** Detection and the injection keyword check are rule-based; expect some misses and false alarms in real use.
  Per-app exclusions (*Settings › Excluded apps*) are the escape hatch.
- **AI quality** depends on Gemini's free tier: it can be slow, busy or out of quota. huh then shows the on-device info alone.

## Build from source

Requirements: Windows 10 1809+ (Windows 11 recommended), the [.NET 10 SDK](https://dotnet.microsoft.com/download).
No Visual Studio needed.

```powershell
git clone https://github.com/syedibrahim369/huh.git
cd huh
dotnet build Huh.slnx -c Debug -p:Platform=x64
dotnet test --project tests/Huh.Tests
src\Huh.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Huh.exe
```

Release build (self-contained, trimmed, ReadyToRun; this is what the zip contains):

```powershell
dotnet publish src/Huh.App/Huh.App.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
# output: src\Huh.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\
```

For AI explanations, get a free key at [aistudio.google.com](https://aistudio.google.com/apikey). Paste it on the consent
screen, where it's stored with DPAPI, or set `GEMINI_API_KEY`.

Developer switches (environment variables):

| Variable | Effect |
|---|---|
| `HUH_AI_FAKE=1` / `=quota` | Canned AI answer or a simulated 429, with no network calls (used by the UI checks) |
| `HUH_DATA_DIR=<folder>` | Use another settings/cache/log folder (test a first run without touching your real settings) |
| `HUH_ANIMATIONS=0` / `=1` | Override Windows' Animation effects setting |
| `HUH_NO_PREWARM=1` | Skip the startup pre-warm (for measuring) |
| `HUH_THEME=Light` / `=Dark` | Force the bubble's theme |

Test tools: `tools/Huh.TestHarness` (`list`, `run <scenario>`), `tools/Huh.Probe` (`score`, `bubble`, `watch`, `snap`,
`motion`, `traymenu`, `capture`, `click`, `scroll`, `expand`). Its screenshots only keep huh's and the harness's own
pixels (it checks what covers the area first), and it waits for 3 s without keyboard or mouse input before anything that changes focus.
Decisions, gotchas and what's left are in [NOTES.md](NOTES.md).

## Roadmap

- **Local AI with Ollama**: the same `IExplainer` interface, so nothing leaves the PC at all (planned; not in 1.0).
- Localized dialog cues (TaskDialogs in other languages).
- A real mixed-DPI test on a second monitor.
- Code signing and an MSIX/winget package.

## Credits

- Designed and directed by Syed Ibrahim Mustafa; built with Claude Code as a pair programmer.
- [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK), [CsWin32](https://github.com/microsoft/CsWin32),
  [xUnit](https://xunit.net/), and Google's [Gemini API](https://ai.google.dev/) (free tier).
- Segoe Fluent Icons (Windows) for the bubble's glyphs. The huh icon is generated by `tools/icons/make-icons.ps1`.

## License

[MIT](LICENSE).
