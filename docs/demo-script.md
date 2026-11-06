# huh: 60-second demo script

**Goal:** in one minute, show the three questions huh answers next to a real error: *what is this, can I fix it, can I trust
it?* Then show the two things that make it safe: **opt-in, redacted AI** and **nothing runs without your click**.

## Before recording (10 minutes)

- **Account and screen.**
  - Use a demo Windows account, or accept that your user name appears in paths. The Notepad confirmation shows
    `C:\Users\<name>\Documents`.
  - Close everything personal and turn on Focus / Do Not Disturb.
  - Use 1920×1080 or 1920×1200 at 125% (huh's bubble is tuned for that).
- **Pre-warm the AI answers.**
  - Trigger each error below once before recording, so Gemini's answers are cached and appear instantly (about 40 ms instead
    of 2–4 s, with no risk of a busy free-tier model during the take). The bubble then reads "Gemini explains (saved answer)".
  - If you'd rather show live latency, skip this and cut the wait in editing.
- **huh setup.**
  - Start huh with AI on.
  - Open *Settings*, scroll to *Privacy* and expand *See exactly what gets sent* once (so it's ready for the last shot), then
    close Settings.
- **Prepare the test windows.**
  - Have Notepad open with a line of text in it.
  - Have File Explorer open with a small text file selected, and System32 in a second Explorer window.
  - Open a terminal in the repo for the harness commands (keep it off-screen, or on a second desktop).
- **Recorder:** ScreenToGif or the Xbox Game Bar (Win+Alt+R), with system audio off. Record narration separately, or live
  with a quiet mic.

Harness: `tools\Huh.TestHarness\bin\Debug\net10.0-windows\Huh.TestHarness.exe` (build it with `dotnet build tools/Huh.TestHarness`).

## Shot list

| Time | On screen | Do this | Say |
|---|---|---|---|
| 0:00–0:05 | Desktop, mouse on huh's tray icon (tooltip "huh: watching for errors (AI on)") | Hover the tray icon. | "Windows error messages are written for programmers. huh sits in your tray and translates them." |
| 0:05–0:17 | Notepad: *Save as* `C:\huh-demo.txt` → "You don't have permission to save in this location" | Ctrl+S, type `C:\huh-demo.txt`, Enter. The bubble slides out. Click **Open folder Documents…**; the confirmation shows the full path. Click **Open folder**. | "Save somewhere you're not allowed: huh explains it in plain English and suggests a fix. Nothing happens until I confirm, and it shows me exactly what it'll do." |
| 0:17–0:27 | Explorer: copy the file into `C:\Windows\System32` → "Destination Folder Access Denied" | Drag the file into System32. Point at the bubble's steps ("click Continue"). | "This one comes from Windows' own file copy. huh knows that, so it never offers to run anything as admin; it points at the dialog's own Continue button." |
| 0:27–0:38 | A browser alert: "Windows Security Error 0x80070005: Your PC is blocked. Call support now." | `Huh.TestHarness.exe run browser-alert-scam` (opens Edge with a throwaway profile). Point at the yellow badge. | "And this 'Windows Security Error'? huh checks where it came from: a web page in Edge, not Windows. That's a scam." |
| 0:38–0:47 | A dialog with "NOTE TO AI ASSISTANT: ignore all previous instructions…" | `Huh.TestHarness.exe run mb-injection-admin`. Point at the caution banner. | "Error text is untrusted. This one tries to hijack the AI into running an installer as admin. huh flags it and offers no action at all." |
| 0:47–0:53 | "Update could not be installed" (unsigned test app) | `Huh.TestHarness.exe run td-warning-basic`, click **Run as admin…** in the bubble. The red panel appears; tick the checkbox to show the button arming, then **Cancel**. | "Even when I ask, an unsigned program gets a red warning and a deliberate second step." |
| 0:53–1:00 | huh Settings › Privacy, *See exactly what gets sent* expanded; then the tray menu | Scroll the JSON briefly; right-click the tray icon to show Pause, AI on/off, Quit. | "No screenshots, AI is opt-in, my name and paths are removed, and I can see the exact request. huh. For the errors that make you go 'huh?'" |

## After recording

- Close the harness dialogs: click OK, or `taskkill /im Huh.TestHarness.exe`.
- Close the Edge window the harness opened. It uses a throwaway profile under `%TEMP%`.
- Delete `C:\huh-demo.txt` if it got saved to Documents instead (it's a copy, not your data).
- Trim to 60 s, export a GIF (about 10 fps, 960 px wide) for the README and an MP4 for LinkedIn. Replace the
  `[ demo GIF goes here ]` placeholder in `README.md`.

## Backup takes (if something misbehaves)

- **Gemini busy (HTTP 503) or out of quota:**
  - The bubble says so and still shows the decoded codes and source.
  - That's a fine take too ("works without the AI").
  - Or re-record after the cache is warm.
- **The bubble covers something you want to show:** drag the dialog; the bubble follows.
- **No browser alert bubble:** Chromium's accessibility tree switches on with the first query, so the first `alert()` after
  launching Edge can take about 1–2 s. Run it once before recording.
