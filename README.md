# huh

A Windows tray app that notices error dialogs anywhere in Windows and slides out a small bubble beside them:
what went wrong in plain English, a one-click fix, and whether the dialog itself is legit (which program raised it,
and whether it's signed).

**Status:** early development. See [NOTES.md](NOTES.md) for decisions and progress.

- Native WinUI 3 / .NET 10
- No screenshots: dialog text is read only through UI Automation
- AI explanations are opt-in; personal info is redacted before anything is sent
- Fix actions come from a fixed allowlist and always need your click
