# M2 bubble checks (2026-11-06 00:57)

Display: 1920x1200 at 125%

**46/46 checks passed**

| | Check | Detail |
|---|---|---|
| ok | mb-error-access-denied: bubble shown | 1700 ms after focus |
| ok | mb-error-access-denied: focus not stolen | foreground=0x4E05DE #32770 pid=11172 |
| ok | mb-error-access-denied: WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW | exstyle=0x8000180 |
| ok | mb-error-access-denied/initial: placed Right, inside work area, no overlap | dialog (812,492)-(1126,716) 314x224 bubble (1139,492)-(1564,738) 425x246 expected (1139,492)-(1564,738) 425x246 |
| ok | mb-error-access-denied: directly above its dialog in z-order | z bubble=4 dialog=5 |
| ok | mb-error-access-denied: topmost matches dialog | dialog topmost=False |
| ok | mb-error-access-denied/right-edge: placed Left, inside work area, no overlap | dialog (1610,300)-(1924,524) 314x224 bubble (1172,300)-(1597,546) 425x246 expected (1172,300)-(1597,546) 425x246 |
| ok | mb-error-access-denied/left-edge: placed Right, inside work area, no overlap | dialog (12,200)-(326,424) 314x224 bubble (339,200)-(764,446) 425x246 expected (339,200)-(764,446) 425x246 |
| ok | mb-error-access-denied/near-bottom: placed Right, inside work area, no overlap | dialog (508,912)-(822,1136) 314x224 bubble (835,884)-(1260,1130) 425x246 expected (835,884)-(1260,1130) 425x246 |
| ok | mb-error-access-denied/top-left-corner: placed Right, inside work area, no overlap | dialog (8,0)-(322,224) 314x224 bubble (335,10)-(760,256) 425x246 expected (335,10)-(760,256) 425x246 |
| ok | mb-error-access-denied/center: placed Right, inside work area, no overlap | dialog (811,458)-(1125,682) 314x224 bubble (1138,458)-(1563,704) 425x246 expected (1138,458)-(1563,704) 425x246 |
| ok | mb-error-access-denied/after-drag: placed Right, inside work area, no overlap | dialog (656,366)-(970,590) 314x224 bubble (983,366)-(1408,612) 425x246 expected (983,366)-(1408,612) 425x246 |
| ok | mb-error-access-denied: unrelated foreground window stays above bubble | z cover=4 bubble=5 |
| ok | mb-error-access-denied: bubble returns above dialog when dialog is refocused | z bubble=4 dialog=5 cover=6 |
| ok | mb-error-access-denied: bubble closes with dialog | dialog closed, 0 ms |
| ok | td-error-command-links: bubble shown | 266 ms after focus |
| ok | td-error-command-links: focus not stolen | foreground=0x1B0596 #32770 pid=28260 |
| ok | td-error-command-links: WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW | exstyle=0x8000180 |
| ok | td-error-command-links/initial: placed Right, inside work area, no overlap | dialog (702,340)-(1200,716) 498x376 bubble (1213,340)-(1638,610) 425x270 expected (1213,340)-(1638,610) 425x270 |
| ok | td-error-command-links: directly above its dialog in z-order | z bubble=4 dialog=5 |
| ok | td-error-command-links: topmost matches dialog | dialog topmost=False |
| ok | td-error-command-links: bubble closes with dialog | app ended (dialog ignores WM_CLOSE), 0 ms |
| ok | mb-error-topmost: bubble shown | 143 ms after focus |
| ok | mb-error-topmost: focus not stolen | foreground=0x2C0794 #32770 pid=6868 |
| ok | mb-error-topmost: WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW | exstyle=0x8000188 |
| ok | mb-error-topmost/initial: placed Right, inside work area, no overlap | dialog (728,501)-(1210,709) 482x208 bubble (1223,501)-(1648,724) 425x223 expected (1223,501)-(1648,724) 425x223 |
| ok | mb-error-topmost: directly above its dialog in z-order | z bubble=3 dialog=4 |
| ok | mb-error-topmost: topmost matches dialog | dialog topmost=True |
| ok | mb-error-topmost: bubble closes with dialog | dialog closed, 0 ms |
| ok | shell-file-in-use: bubble shown | 261 ms after focus |
| ok | shell-file-in-use: focus not stolen | foreground=0x15063A OperationStatusWindow pid=15904 |
| ok | shell-file-in-use: WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW | exstyle=0x8000180 |
| ok | shell-file-in-use/initial: placed Right, inside work area, no overlap | dialog (273,321)-(834,661) 561x340 bubble (847,321)-(1272,591) 425x270 expected (847,321)-(1272,591) 425x270 |
| ok | shell-file-in-use: directly above its dialog in z-order | z bubble=4 dialog=5 |
| ok | shell-file-in-use: topmost matches dialog | dialog topmost=False |
| ok | shell-file-in-use: bubble closes with dialog | dialog closed, 0 ms |
| ok | mb-error-owned: bubble shown | 85 ms after focus |
| ok | mb-error-owned: focus not stolen | foreground=0x2A0C9C #32770 pid=3400 |
| ok | mb-error-owned: WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW | exstyle=0x8000180 |
| ok | mb-error-owned/initial: placed Right, inside work area, no overlap | dialog (792,501)-(1148,709) 356x208 bubble (1161,501)-(1586,701) 425x200 expected (1161,501)-(1586,701) 425x200 |
| ok | mb-error-owned: directly above its dialog in z-order | z bubble=4 dialog=5 |
| ok | mb-error-owned: topmost matches dialog | dialog topmost=False |
| ok | mb-error-owned: bubble hides when owner is minimized |  |
| ok | mb-error-owned: bubble returns when owner is restored |  |
| ok | mb-error-owned/after-restore: placed Right, inside work area, no overlap | dialog (792,501)-(1148,709) 356x208 bubble (1161,501)-(1586,701) 425x200 expected (1161,501)-(1586,701) 425x200 |
| ok | mb-error-owned: bubble closes with dialog | dialog closed, 0 ms |
