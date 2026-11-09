// WinampGuard: runs a 32-bit program under a minimal debugger. If the program
// dies, it records why and shows an error prompt instead of a silent close.
// It cannot keep the program alive: once the heap is corrupt, carrying on risks
// writing damaged data to the media library.
//
// Usage: WinampGuard.exe <config file>
// Config lines: exe=, workdir=, args=, log=, prompt=0|1, shield=0|1
// shield=1 skips invalid NXStringRelease calls (Winamp 5.9.2.10042 nxlite.dll only; very slow).
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class WinampGuard {
  [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, byte[] si, byte[] pi);
  [DllImport("kernel32", SetLastError=true)]
  static extern bool WaitForDebugEvent(IntPtr ev, uint ms);
  [DllImport("kernel32", SetLastError=true)]
  static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
  [DllImport("kernel32", SetLastError=true)]
  static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr n, out IntPtr read);
  [DllImport("kernel32", SetLastError=true)]
  static extern bool Wow64GetThreadContext(IntPtr t, IntPtr ctx);
  [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern uint GetFinalPathNameByHandleW(IntPtr f, StringBuilder sb, uint n, uint flags);
  [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32", SetLastError=true)]
  static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr n, out IntPtr written);
  [DllImport("kernel32", SetLastError=true)]
  static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr n);
  [DllImport("kernel32", SetLastError=true)]
  static extern bool Wow64SetThreadContext(IntPtr t, IntPtr ctx);
  [DllImport("user32", CharSet=CharSet.Unicode)]
  static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

  class Mod { public ulong Base; public ulong Size; public string Name; }
  static List<Mod> mods = new List<Mod>();
  static Dictionary<uint, IntPtr> threads = new Dictionary<uint, IntPtr>();
  static IntPtr proc;
  static StreamWriter w;
  static string report;  // set once, by the first fatal event

  static void L(string s) { w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); }

  static byte[] Rd(ulong a, int n) {
    byte[] b = new byte[n]; IntPtr r;
    return ReadProcessMemory(proc, (IntPtr)(long)a, b, (IntPtr)n, out r) ? b : null;
  }
  static string Sym(ulong a) {
    foreach (Mod m in mods) if (a >= m.Base && a < m.Base + m.Size)
      return Path.GetFileName(m.Name) + "+0x" + (a - m.Base).ToString("X");
    return null;
  }
  static void AddMod(IntPtr hFile, ulong b) {
    StringBuilder sb = new StringBuilder(1024);
    string name = "?";
    if (hFile != IntPtr.Zero && GetFinalPathNameByHandleW(hFile, sb, 1024, 0) > 0) name = sb.ToString();
    if (hFile != IntPtr.Zero) CloseHandle(hFile);
    ulong size = 0;
    byte[] h = Rd(b, 0x400);
    if (h != null) {
      int nt = BitConverter.ToInt32(h, 0x3C);
      if (nt > 0 && nt + 0x54 < h.Length) size = BitConverter.ToUInt32(h, nt + 0x50);
    }
    mods.Add(new Mod { Base = b, Size = size, Name = name });
    L("LOAD " + name + " @" + b.ToString("X") + " size=" + size.ToString("X"));
    if (shield && Path.GetFileName(name).ToLowerInvariant() == "nxlite.dll") SetReleaseHook(b);
  }

  // --- Interception of nxlite!NXStringRelease ---
  // NXStringRelease(self) decrements the counter at self. Called with a pointer that
  // is not a counted string, that write lands on a heap header. The hook checks the
  // counter first and, when it cannot be one, returns without touching memory.
  const uint ReleaseRva = 0x3739;
  static bool shield;
  static ulong releaseAddr;
  static long releaseCalls, badReleases;
  static Dictionary<string, int> badBySite = new Dictionary<string, int>();
  static bool walkQuiet;

  static void SetReleaseHook(ulong modBase) {
    ulong a = modBase + ReleaseRva;
    byte[] orig = Rd(a, 1);
    if (orig == null || orig[0] != 0x55) { L("HOOK NOT SET: unexpected code at NXStringRelease"); return; }
    IntPtr n;
    if (!WriteProcessMemory(proc, (IntPtr)(long)a, new byte[] { 0xCC }, (IntPtr)1, out n)) { L("HOOK NOT SET: write failed " + Marshal.GetLastWin32Error()); return; }
    FlushInstructionCache(proc, (IntPtr)(long)a, (IntPtr)1);
    releaseAddr = a;
    L("HOOK SET on NXStringRelease at " + a.ToString("X"));
  }

  static void OnRelease(uint tid) {
    IntPtr t;
    if (!threads.TryGetValue(tid, out t)) { L("HOOK: no thread handle"); return; }
    IntPtr ctx = Marshal.AllocHGlobal(716 + 16);
    IntPtr al = (IntPtr)(((long)ctx + 15) & ~15L);
    try {
      for (int i = 0; i < 716; i++) Marshal.WriteByte(al, i, 0);
      Marshal.WriteInt32(al, 0, 0x10003);
      if (!Wow64GetThreadContext(t, al)) { L("HOOK: no context " + Marshal.GetLastWin32Error()); return; }
      uint ebp = (uint)Marshal.ReadInt32(al, 0xB4), esp = (uint)Marshal.ReadInt32(al, 0xC4);
      releaseCalls++;
      byte[] top = Rd(esp, 8);
      uint ret = top == null ? 0 : BitConverter.ToUInt32(top, 0);
      uint self = top == null ? 0 : BitConverter.ToUInt32(top, 4);
      byte[] rc = self == 0 ? null : Rd(self, 4);
      uint refCount = rc == null ? 1 : BitConverter.ToUInt32(rc, 0);
      if (refCount == 0 || refCount > 0x100000) {
        badReleases++;
        LogBadRelease(ebp, ret, self, refCount);
        Marshal.WriteInt32(al, 0xB0, 0);                 // eax = 0
        Marshal.WriteInt32(al, 0xB8, (int)ret);          // eip = caller
        Marshal.WriteInt32(al, 0xC4, (int)(esp + 4));    // pop return address (cdecl)
      } else {
        IntPtr n;
        esp -= 4;                                        // perform the replaced "push ebp"
        WriteProcessMemory(proc, (IntPtr)(long)esp, BitConverter.GetBytes(ebp), (IntPtr)4, out n);
        Marshal.WriteInt32(al, 0xC4, (int)esp);
        Marshal.WriteInt32(al, 0xB8, (int)(releaseAddr + 1));
      }
      if (!Wow64SetThreadContext(t, al)) L("HOOK: set context failed " + Marshal.GetLastWin32Error());
    } finally { Marshal.FreeHGlobal(ctx); }
  }

  static void LogBadRelease(uint ebp, uint ret, uint self, uint refCount) {
    uint ignored;
    walkQuiet = true;
    List<string> frames = Walk(ebp, out ignored);
    walkQuiet = false;
    string site = Sym(ret) ?? ret.ToString("X8");
    if (frames.Count > 0) site += " <- " + frames[0];
    if (frames.Count > 1) site += " <- " + frames[1];
    int seen;
    badBySite.TryGetValue(site, out seen);
    badBySite[site] = seen + 1;
    if (seen >= 3) return;
    byte[] s = Rd(self + 8, 160);
    string text = s == null ? "(unreadable)" : Encoding.Unicode.GetString(s);
    int z = text.IndexOf('\0');
    if (z >= 0) text = text.Substring(0, z);
    L("BAD RELEASE blocked: value=" + refCount.ToString("X8") + " at " + self.ToString("X8") + " caller " + site);
    L("  text: " + text);
    for (int i = 0; i < frames.Count && i < 24; i++) L("  frame " + i + " " + frames[i]);
  }

  static bool IsSystem(string sym) {
    if (sym == null) return false;  // not in a module: generated code, treat as program code
    string s = sym.ToLowerInvariant();
    return s.StartsWith("ntdll") || s.StartsWith("ucrtbase") || s.StartsWith("kernel") ||
           s.StartsWith("user32") || s.StartsWith("comctl32") || s.StartsWith("win32u") || s.StartsWith("msvcr");
  }

  // Walks the EBP chain. Returns the frame names; heapArg is the first argument
  // of the innermost system function called from program code (free's pointer).
  static List<string> Walk(uint ebp, out uint heapArg) {
    List<string> frames = new List<string>();
    heapArg = 0;
    uint fp = ebp;
    for (int depth = 0; depth < 64 && fp != 0; depth++) {
      byte[] fr = Rd(fp, 12);
      if (fr == null) break;
      uint nextFp = BitConverter.ToUInt32(fr, 0), ret = BitConverter.ToUInt32(fr, 4);
      string s = Sym(ret) ?? ret.ToString("X8");
      frames.Add(s);
      byte[] fa = Rd(fp + 8, 24);
      if (fa != null && report == null && !walkQuiet) {
        StringBuilder sa = new StringBuilder();
        for (int i = 0; i < 24; i += 4) sa.Append(BitConverter.ToUInt32(fa, i).ToString("X8")).Append(' ');
        L("  args of function returning to " + s + ": " + sa);
      }
      if (heapArg == 0 && !IsSystem(Sym(ret))) heapArg = BitConverter.ToUInt32(fr, 8);
      if (nextFp <= fp) break;
      fp = nextFp;
    }
    return frames;
  }

  static void DumpAround(uint ptr, List<string> texts) {
    L("MEMORY around block " + ptr.ToString("X8") + " (offset 0 = start of the block being freed):");
    StringBuilder ascii = new StringBuilder(), wide = new StringBuilder();
    for (int off = -0x200; off < 0x400; off += 16) {
      ulong a = (ulong)((long)ptr + off);
      byte[] d = Rd(a, 16);
      if (d == null) { L("  " + off.ToString("+0000;-0000") + " (unreadable)"); Flush(ascii, wide, texts); continue; }
      StringBuilder hex = new StringBuilder(), txt = new StringBuilder();
      foreach (byte b in d) { hex.Append(b.ToString("X2")).Append(' '); txt.Append(b >= 32 && b < 127 ? (char)b : '.'); }
      L("  " + off.ToString("+0000;-0000") + " " + hex + " " + txt);
      foreach (byte b in d) {
        if (b >= 32 && b < 127) ascii.Append((char)b); else { Take(ascii, 6, texts); }
      }
      for (int i = 0; i + 1 < 16; i += 2) {
        if (d[i + 1] == 0 && d[i] >= 32 && d[i] < 127) wide.Append((char)d[i]); else { Take(wide, 4, texts); }
      }
    }
    Flush(ascii, wide, texts);
  }
  static void Flush(StringBuilder a, StringBuilder b, List<string> t) { Take(a, 6, t); Take(b, 4, t); }
  static void Take(StringBuilder sb, int min, List<string> texts) {
    if (sb.Length >= min && !texts.Contains(sb.ToString())) texts.Add(sb.ToString());
    sb.Length = 0;
  }

  static string CodeName(uint code) {
    switch (code) {
      case 0xC0000374: return "heap memory corruption: earlier code overwrote memory it did not own";
      case 0xC0000005: return "access violation: invalid memory read or write";
      case 0xC0000409: return "stack buffer overrun";
      case 0xC00000FD: return "stack overflow";
      case 0xC0000602: return "fail-fast abort";
      default: return "unhandled exception";
    }
  }

  static ulong[] exInfo = new ulong[0];

  // Windows passes a pointer to its heap failure record as an exception parameter.
  static void DumpHeapFailureInfo() {
    L("EXCEPTION PARAMS: " + exInfo.Length);
    foreach (ulong p in exInfo) {
      L("  param " + p.ToString("X"));
      if (p < 0x10000 || p > 0xFFFFFFFF) continue;
      for (int off = 0; off < 0x100; off += 16) {
        byte[] d = Rd(p + (ulong)off, 16);
        if (d == null) { L("    +" + off.ToString("X3") + " (unreadable)"); break; }
        L("    +" + off.ToString("X3") + " " + BitConverter.ToUInt32(d, 0).ToString("X8") + " " + BitConverter.ToUInt32(d, 4).ToString("X8") + " " + BitConverter.ToUInt32(d, 8).ToString("X8") + " " + BitConverter.ToUInt32(d, 12).ToString("X8"));
      }
    }
  }

  static void Capture(uint code, ulong addr, uint tid) {
    IntPtr t;
    if (!threads.TryGetValue(tid, out t)) { L("  (no thread handle)"); return; }
    IntPtr ctx = Marshal.AllocHGlobal(716 + 16);
    IntPtr al = (IntPtr)(((long)ctx + 15) & ~15L);
    try {
      for (int i = 0; i < 716; i++) Marshal.WriteByte(al, i, 0);
      Marshal.WriteInt32(al, 0, 0x10003);
      if (!Wow64GetThreadContext(t, al)) { L("  (no x86 context, err " + Marshal.GetLastWin32Error() + ")"); return; }
      uint ebp = (uint)Marshal.ReadInt32(al, 0xB4);
      uint heapArg;
      List<string> frames = Walk(ebp, out heapArg);
      for (int i = 0; i < frames.Count; i++) L("  frame " + i + " " + frames[i]);

      if (report != null) return;  // keep the first capture; later events are the same failure
      List<string> texts = new List<string>();
      if (code == 0xC0000374) DumpHeapFailureInfo();
      if (code == 0xC0000374 && heapArg != 0) DumpAround(heapArg, texts);
      if (texts.Count > 0) L("TEXT near block: " + string.Join(" | ", texts.ToArray()));

      StringBuilder sb = new StringBuilder();
      sb.AppendLine("Winamp has crashed and cannot continue.");
      sb.AppendLine();
      sb.AppendLine("Reason: 0x" + code.ToString("X8") + ", " + CodeName(code));
      sb.AppendLine("Raised at: " + (Sym(addr) ?? addr.ToString("X")));
      sb.AppendLine();
      sb.AppendLine("Program code on the call chain:");
      int shown = 0;
      foreach (string f in frames) if (!IsSystem(f) && shown++ < 8) sb.AppendLine("   " + f);
      if (texts.Count > 0) {
        sb.AppendLine();
        sb.AppendLine("Text found near the damaged memory:");
        int n = 0;
        foreach (string s in texts) if (n++ < 8) sb.AppendLine("   " + (s.Length > 90 ? s.Substring(0, 90) + "..." : s));
      }
      report = sb.ToString();
    } finally { Marshal.FreeHGlobal(ctx); }
  }

  static void Finish(string body, string log, bool prompt) {
    string text = body + "\r\nFull report:\r\n" + log;
    L("PROMPT\r\n" + text);
    if (prompt) MessageBoxW(IntPtr.Zero, text, "WinampGuard: Winamp crashed", 0x10 /*ICONERROR*/ | 0x10000 /*SETFOREGROUND*/ | 0x40000 /*TOPMOST*/);
  }

  public static int Main(string[] argv) {
    Dictionary<string, string> cfg = new Dictionary<string, string>();
    foreach (string line in File.ReadAllLines(argv[0])) {
      int i = line.IndexOf('=');
      if (i > 0) cfg[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
    }
    string exe = cfg["exe"], cwd = cfg["workdir"], log = cfg["log"];
    string args; cfg.TryGetValue("args", out args);
    string p; bool prompt = !(cfg.TryGetValue("prompt", out p) && p == "0");
    shield = cfg.TryGetValue("shield", out p) && p == "1";

    w = new StreamWriter(log, false, new UTF8Encoding(false)); w.AutoFlush = true;
    byte[] si = new byte[104]; si[0] = 104;
    byte[] pi = new byte[24];
    StringBuilder cmd = new StringBuilder("\"" + exe + "\" " + args, 4096);
    if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 2 /*DEBUG_ONLY_THIS_PROCESS*/, IntPtr.Zero, cwd, si, pi)) {
      int err = Marshal.GetLastWin32Error();
      L("CreateProcess failed " + err);
      Finish("WinampGuard could not start Winamp (Windows error " + err + ").\r\n", log, prompt);
      w.Close();
      return 1;
    }
    L("STARTED pid " + BitConverter.ToUInt32(pi, 16));
    IntPtr ev = Marshal.AllocHGlobal(256);
    int logged = 0, initialBps = 0;
    uint exitCode = 0;
    bool running = true;
    while (running) {
      if (!WaitForDebugEvent(ev, 0xFFFFFFFF)) { L("WaitForDebugEvent failed " + Marshal.GetLastWin32Error()); break; }
      uint kind = (uint)Marshal.ReadInt32(ev, 0), pid = (uint)Marshal.ReadInt32(ev, 4), tid = (uint)Marshal.ReadInt32(ev, 8);
      uint status = 0x00010002; // DBG_CONTINUE
      switch (kind) {
        case 1: {
          uint code = (uint)Marshal.ReadInt32(ev, 16);
          ulong addr = (ulong)Marshal.ReadInt64(ev, 32);
          uint first = (uint)Marshal.ReadInt32(ev, 168);
          if ((code == 0x80000003 || code == 0x4000001F) && releaseAddr != 0 && addr == releaseAddr) {
            OnRelease(tid);
            break;
          }
          if (code == 0x80000003 || code == 0x4000001F) {
            if (initialBps < 2) { initialBps++; break; }
            L("BREAK " + code.ToString("X8") + " at " + (Sym(addr) ?? addr.ToString("X")));
            break;
          }
          status = 0x80010001; // DBG_EXCEPTION_NOT_HANDLED
          if (code == 0x406D1388 || code == 0xE06D7363) break;  // thread naming, C++ throw
          bool fatal = first == 0 || code == 0xC0000374 || code == 0xC0000409 || code == 0xC00000FD || code == 0xC0000602;
          if (fatal) {
            L("FATAL " + code.ToString("X8") + (first != 0 ? " first-chance" : " SECOND-CHANCE") + " at " + (Sym(addr) ?? addr.ToString("X")) + " thread " + tid);
            int np = Math.Min(Marshal.ReadInt32(ev, 40), 15);
            exInfo = new ulong[np];
            for (int i = 0; i < np; i++) exInfo[i] = (ulong)Marshal.ReadInt64(ev, 48 + 8 * i);
            Capture(code, addr, tid);
          } else if (logged++ < 60) {
            L("exception " + code.ToString("X8") + " first-chance at " + (Sym(addr) ?? addr.ToString("X")));
          }
          break;
        }
        case 2: threads[tid] = Marshal.ReadIntPtr(ev, 16); break;
        case 3: {
          IntPtr hf = Marshal.ReadIntPtr(ev, 16);
          proc = Marshal.ReadIntPtr(ev, 24);
          threads[tid] = Marshal.ReadIntPtr(ev, 32);
          AddMod(hf, (ulong)Marshal.ReadInt64(ev, 40));
          break;
        }
        case 4: threads.Remove(tid); break;
        case 5:
          exitCode = (uint)Marshal.ReadInt32(ev, 16);
          L("EXIT code=0x" + exitCode.ToString("X8"));
          running = false;
          break;
        case 6: AddMod(Marshal.ReadIntPtr(ev, 16), (ulong)Marshal.ReadInt64(ev, 24)); break;
        case 7: {
          ulong b = (ulong)Marshal.ReadInt64(ev, 16);
          mods.RemoveAll(m => m.Base == b);
          break;
        }
      }
      ContinueDebugEvent(pid, tid, status);
    }

    L("RELEASE STATS calls=" + releaseCalls + " blocked=" + badReleases);
    foreach (KeyValuePair<string, int> kv in badBySite) L("  blocked " + kv.Value + "x from " + kv.Key);

    // Report only after the process is gone, so the prompt never freezes a live window.
    if (report != null) Finish(report, log, prompt);
    else if (exitCode >= 0xC0000000) Finish("Winamp closed abnormally with code 0x" + exitCode.ToString("X8") + ", and no crash details could be captured.\r\n", log, prompt);
    w.Close();
    return 0;
  }
}
