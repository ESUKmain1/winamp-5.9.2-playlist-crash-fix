# WinampGuard

A small Windows debugger used to diagnose the crash. It starts a 32-bit program, and if the program dies, it writes a report and shows an error box. It can't keep a crashed program alive.

You don't need this tool to apply the fix.

## Build

No extra tools are needed on Windows with .NET Framework 4:

```powershell
C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /target:winexe /platform:x64 /optimize+ /out:WinampGuard.exe WinampGuard.cs
```

## Run

Copy `guard.example.cfg`, edit the paths, then run:

```powershell
.\WinampGuard.exe C:\Path\To\guard.cfg
```

| Setting | Meaning |
|---|---|
| `exe` | Full path to `winamp.exe` |
| `workdir` | Folder to start it in |
| `args` | Command-line arguments, can be empty |
| `log` | Report file to write |
| `prompt` | `1` shows the error box, `0` only writes the report |
| `shield` | `1` intercepts `NXStringRelease` and skips invalid releases |

## Notes

- `shield=1` pauses Winamp on every string release, so it's very slow with a large library. The hook is specific to the `nxlite.dll` shipped with Winamp 5.9.2.10042.
- **Reports contain personal data**, including file paths, your Windows username and song titles. Remove these before sharing a report publicly.
