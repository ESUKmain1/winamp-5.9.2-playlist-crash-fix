# Fix for Winamp 5.9.2 closing without an error

Winamp 5.9.2 build 10042 can close instantly, with no error message, when you double-click tracks in the Media Library. This repository explains the cause and provides a script that fixes your own installed copy.

**This project is not affiliated with or endorsed by Winamp.** It contains no Winamp code or program files.

## Does this apply to you?

It may, if all of these are true:

- You run Winamp **5.9.2 build 10042** (the script checks the exact file).
- Winamp disappears with no error when you play tracks from the Media Library.
- `_crash.log` stays empty or no crash report is produced.

With a large playlist, it can happen on the first double-click. With a small or empty playlist, it may take two or more. It doesn't depend on which song you pick.

If you're on another Winamp version, the script will refuse to run and won't change anything.

## Install the fix

You'll need Windows and administrator rights.

1. Close Winamp.
2. Download this repository and extract it.
3. Open **PowerShell as administrator** and go to the extracted folder.
4. Run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\patch\apply-fix.ps1
   ```

If Winamp is installed somewhere other than `C:\Program Files (x86)\Winamp`, add `-WinampDir "D:\Path\To\Winamp"`.

The script:

1. Confirms `winamp.exe` is exactly the expected file (by SHA-256).
2. Saves a verified backup beside it, named `winamp.exe.original-5.9.2.10042.bak`.
3. Changes 6 bytes.
4. Confirms the result is exactly the expected file. If not, it restores the original.

## Undo the fix

Close Winamp, then run this in PowerShell as administrator:

```powershell
powershell -ExecutionPolicy Bypass -File .\patch\undo-fix.ps1
```

## What the fix changes

| | |
|---|---|
| File | `winamp.exe`, version 5.9.2.10042 |
| Original SHA-256 | `ADDF561FCDC496C1318DDC3586352AA7F6C1FEB684A9E8FFA285409BEAC5B446` |
| Fixed SHA-256 | `CC98F8C138D75B4A2843E64EF5B0F2891509C400038A5768A4EC5A3537D42040` |
| File offset | `0xAD297` (RVA `0xADE97`) |
| Original bytes | `FF 15 D8 76 4E 00` |
| New bytes | `90 90 90 90 90 90` |

The fix removes one faulty call made when a playlist entry is cleared. See [the technical write-up](docs/INVESTIGATION.md) for the details.

## Before you use it

- **Limited testing:** this was tested on one Windows 11 PC with a library and playlist of about 33,000 to 36,000 items. Results elsewhere are unknown.
- **The digital signature stops validating:** this is expected after any change to a signed program. Some antivirus tools may flag the patched file.
- **A little memory stays in use:** titles of cleared playlist entries remain in memory until Winamp closes.
- **Updates replace the fix:** a Winamp update or reinstall overwrites `winamp.exe`.
- **Check Winamp's licence terms:** make sure modifying your copy is acceptable under the terms that apply to you.
- **No warranty:** you use this at your own risk. Keep the backup.

## Why not a patched download?

`winamp.exe` is copyrighted, so this repository doesn't include it. The script changes the copy you already have.

## Diagnostic tool

[`tools/WinampGuard`](tools/WinampGuard) is the small debugger used to trace this bug. It runs Winamp, shows an error box when it crashes, and writes a report. You don't need it for the fix.

## For the Winamp developers

The likely source-level fix is to make the title field's allocation and release agree: either create it with `ndestring_wcsdup` or free it with the matching plain-string function. Details are in [the technical write-up](docs/INVESTIGATION.md).

## Licence

The scripts, tool source and documentation here are under the [MIT Licence](LICENSE). Winamp is the property of its owners and isn't covered by this licence.
