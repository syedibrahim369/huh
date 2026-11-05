# M1 detection report (2026-11-06 00:05)

- Error dialogs: **28/28 detected and classified as error (100%)**; 28/28 detected at all
- Non-error dialogs: 6/6 correctly ignored, 0 false positive(s)
- Latency (event to classified), detected error dialogs: p50 166 ms, p95 369 ms, max 510 ms

| Result | Scenario | Expected | Got | Kind | Process | Latency | Signals |
|---|---|---|---|---|---|---|---|
| PASS | `mb-error-access-denied` | error | Error | Win32Dialog | Huh.TestHarness | 369 ms | class:#32770 uia:isdialog icon:error text:could not code:0x80070005 |
| PASS | `mb-error-file-not-found` | error | Error | Win32Dialog | Huh.TestHarness | 159 ms | class:#32770 uia:isdialog icon:error text:cannot |
| PASS | `mb-warning-disk-full` | error | Error | Win32Dialog | Huh.TestHarness | 169 ms | class:#32770 uia:isdialog icon:warning text:failed |
| PASS | `mb-error-icon-only` | error | Error | Win32Dialog | Huh.TestHarness | 177 ms | class:#32770 uia:isdialog icon:error code:Code 43 |
| PASS | `mb-error-hresult-only` | error | Error | Win32Dialog | Huh.TestHarness | 165 ms | class:#32770 uia:isdialog code:0x8007045D |
| PASS | `mb-error-retry-cancel` | error | Error | Win32Dialog | Huh.TestHarness | 170 ms | class:#32770 uia:isdialog icon:error text:not ready |
| PASS | `mb-error-abort-retry-ignore` | error | Error | Win32Dialog | Huh.TestHarness | 176 ms | class:#32770 uia:isdialog icon:warning text:cannot |
| PASS | `mb-error-system-modal` | error | Error | Win32Dialog | Huh.TestHarness | 155 ms | class:#32770 uia:isdialog icon:error text:fatal |
| PASS | `mb-error-topmost` | error | Error | Win32Dialog | Huh.TestHarness | 153 ms | class:#32770 uia:isdialog icon:error text:failed |
| PASS | `mb-error-long-stack` | error | Error | Win32Dialog | Huh.TestHarness | 158 ms | class:#32770 uia:isdialog icon:error text:unhandled |
| PASS | `mb-error-ntstatus` | error | Error | Win32Dialog | Huh.TestHarness | 159 ms | class:#32770 uia:isdialog icon:error text:error code:code: 0xC0000005 |
| PASS | `mb-error-german` | error | Error | Win32Dialog | Huh.TestHarness | 150 ms | class:#32770 uia:isdialog icon:error |
| PASS | `mb-error-owned` | error | Error | Win32Dialog | Huh.TestHarness | 149 ms | class:#32770 uia:isdialog icon:error text:failed |
| PASS | `td-error-basic` | error | Error | TaskDialog | Huh.TestHarness | 175 ms | class:#32770 uia:isdialog text:couldn't code:error 0x800704CF |
| PASS | `td-warning-basic` | error | Error | TaskDialog | Huh.TestHarness | 186 ms | class:#32770 uia:isdialog text:could not |
| PASS | `td-error-command-links` | error | Error | TaskDialog | Huh.TestHarness | 214 ms | class:#32770 uia:isdialog text:failed |
| PASS | `td-error-hyperlink` | error | Error | TaskDialog | Huh.TestHarness | 185 ms | class:#32770 uia:isdialog text:couldn't |
| PASS | `td-shield-elevation` | error | Error | TaskDialog | Huh.TestHarness | 220 ms | class:#32770 uia:isdialog text:denied text:administrator permission |
| PASS | `shell-file-in-use` | error | Error | OtherWindow | Huh.TestHarness | 176 ms | class:OperationStatusWindow text:can't |
| PASS | `shell-access-denied` | error | Error | OtherWindow | Huh.TestHarness | 162 ms | class:OperationStatusWindow text:denied text:administrator permission |
| PASS | `shell-path-not-found` | error | Error | TaskDialog | Huh.TestHarness | 167 ms | class:#32770 uia:isdialog text:cannot |
| PASS | `rundll32-missing-dll` | error | Error | Win32Dialog | rundll32 | 155 ms | class:#32770 uia:isdialog icon:error text:problem |
| PASS | `regsvr32-missing-dll` | error | Error | Win32Dialog | regsvr32 | 162 ms | class:#32770 uia:isdialog icon:error text:failed |
| PASS | `msiexec-bad-package` | error | Error | TaskDialog | msiexec | 510 ms | class:#32770 uia:isdialog text:could not |
| PASS | `hard-error-dll-not-found` | error | Error | Win32Dialog | csrss | 144 ms | class:#32770 uia:isdialog icon:error text:error |
| PASS | `hard-error-bad-image` | error | Error | Win32Dialog | csrss | 131 ms | class:#32770 uia:isdialog icon:error text:exception code:0xc000007b |
| PASS | `custom-window-error` | error | Error | OtherWindow | Huh.TestHarness | 157 ms | class:HuhHarnessWindow text:error |
| PASS | `mb-error-from-thread` | error | Error | Win32Dialog | Huh.TestHarness | 166 ms | class:#32770 uia:isdialog icon:error text:failed |
| PASS | `neg-info-done` | not error | Information | Win32Dialog | Huh.TestHarness | 142 ms | class:#32770 uia:isdialog icon:information |
| PASS | `neg-question-save` | not error | Question | Win32Dialog | Huh.TestHarness | 151 ms | class:#32770 uia:isdialog icon:question phrasing:question |
| PASS | `neg-confirm-delete` | not error | Question | Win32Dialog | Huh.TestHarness | 173 ms | class:#32770 uia:isdialog icon:warning phrasing:question |
| PASS | `neg-td-info` | not error | Information | TaskDialog | Huh.TestHarness | 159 ms | class:#32770 uia:isdialog |
| PASS | `neg-about` | not error | Information | Win32Dialog | Huh.TestHarness | 153 ms | class:#32770 uia:isdialog |
| PASS | `neg-plain-window` | not error | not detected |  |  |  |  |
