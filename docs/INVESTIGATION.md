# Technical write-up

This describes how the crash in Winamp 5.9.2.10042 was traced. It was done on compiled binaries, without source code, so statements about what the code is meant to do are inferred from its behaviour and machine code.

## Summary

When a playlist entry is cleared, `winamp.exe` calls `ndestring_release` on the entry's title. In the observed cases the title is a plain heap string, not a reference-counted one. The release decrements the 32-bit value 8 bytes before the string, which is the start of the heap block's header. The damage is detected later, when a neighbouring block is freed, and Windows terminates the process with `0xC0000374`.

## Symptom

Winamp closes with no error. The crash is a heap fail-fast on the main UI thread, so Winamp's own crash logger doesn't run and `_crash.log` stays empty.

The detecting call chain is always the same, which makes the title formatter look guilty. It only happens to be the next code to free memory beside the damage:

```text
ntdll!RtlFreeHeap (reports 0xC0000374)
ucrtbase!free
winamp.exe+0x930F6   tag provider dispatch, case 20: free the tag string
tagz.w5s             title formatting
winamp.exe+0x92A97
winamp.exe+0x92E21
winamp.exe+0x49C4D   handling of WM_WA_IPC message 298
winamp.exe+0xA9B48   main window procedure
```

## Evidence

### The block being freed is healthy

With a debugger attached, Windows enables heap tail checking. The block being freed held a valid `%filename%` string and had an intact header and intact `AB` tail bytes, so it wasn't overrun.

### Windows flags the next block

The heap failure record passed as the exception parameter reported failure type 4, which is believed to mean entry corruption. In every crash where the record was captured, its address was the header of the block directly after the one being freed.

### The damage is exactly -1

The first four bytes of an x86 heap entry are XOR-encoded and hold `Size`, `Flags` and a checksum (`SmallTagIndex`). Example from one run:

| Header | Encoded | Decoded size | Checksum result |
|---|---|---|---|
| Intact neighbours | various | correct | valid |
| Flagged block | `18 D7 BC DB` | `0x0A` | invalid |
| Flagged block with byte 0 as `0x19` | `19 D7 BC DB` | `0x0B`, matching the block's actual extent | valid |

The first byte is exactly one too low. In runs with a full playlist, every string block in view that was still allocated showed the same offset. The strings and tail bytes around them were untouched, which points away from an overrun and towards a decrement at a precise address.

### The decrement site

`nde.dll!ndestring_release` subtracts 8 from the string pointer and calls `nxlite.dll!NXStringRelease`, which runs `lock xadd [self], -1`. For a plain heap string, `str - 8` is the heap entry header.

### Finding the caller

The guard placed a breakpoint on `NXStringRelease`. If the value at `self` couldn't be a reference count, it logged the caller and skipped the decrement.

| Measure | Result |
|---|---|
| Total releases in the session | 1,610,387 |
| Invalid releases blocked | 34,086 |
| Blocked from `winamp.exe+0xADE9D` | 34,086 (all of them) |
| Crash | none; clean exit, code 0 |

The logged strings were all `Artist - Title` style titles.

### The faulty code

The cleanup function around RVA `0xADE3E` releases three fields of a playlist entry through a function pointer at `0x4E76D8`:

```text
ff 36                 push  dword ptr [esi]
ff 15 d8 76 4e 00     call  [0x4E76D8]        ; release
ff 76 08              push  dword ptr [esi+8]
89 1e                 mov   [esi], ebx
ff 15 d8 76 4e 00     call  [0x4E76D8]        ; release
ff 76 04              push  dword ptr [esi+4]
89 5e 08              mov   [esi+8], ebx
ff 15 d8 76 4e 00     call  [0x4E76D8]        ; release, RVA 0xADE97: the faulty call
83 c4 0c              add   esp, 0Ch
```

Every blocked release returned to the instruction after the third call.

## Experiments

| Test | Result | Conclusion |
|---|---|---|
| Empty playlist | First double-click worked, second crashed | Playlist size only changes the timing |
| `ml_history` not loaded | Crashed the same way | Not the cause |
| Invalid releases blocked by the guard | No crash | Confirms the mechanism, but too slow for normal use |
| The third call replaced with NOPs | No crash, responsive | Fixes the problem in testing |

Earlier testing also ruled out the media file itself, the drive, the skin, the configured title format, the JPEG decoder and `in_ffsox`.

## Why replacing the call is safe here

The call is `cdecl`, and the three pushed arguments are removed together by the later `add esp, 0Ch`, so replacing the 6-byte call with NOPs leaves the stack balanced. The field is still cleared by `mov [esi+4], ebx`.

For plain strings, the original call only decremented the heap header and never freed the string, so they weren't being reclaimed before either. Any valid counted string reaching this site would also stop being released and would stay allocated until exit.

## Known gaps

- It's unknown why this affects some installations and not others. Heap layout may decide whether the damage is ever detected, and something in a particular setup may also contribute.
- The code that creates the title wasn't identified, so the proper source-level fix is an informed suggestion.
- Testing was on one PC running Windows 11 build 26100.
