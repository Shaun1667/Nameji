param([int]$ProcId)
if (-not ("EW" -as [type])) {
Add-Type @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class EW {
  public delegate bool CB(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(CB cb, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr p, CB cb, IntPtr l);
  [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  static string D(IntPtr h, string ind) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); RECT r; GetWindowRect(h, out r);
    return string.Format("{0}0x{1:X} {2} vis={3} rect=({4},{5},{6},{7}) style=0x{8:X8} ex=0x{9:X8}", ind, h.ToInt64(), sb, IsWindowVisible(h), r.L, r.T, r.R, r.B, GetWindowLong(h,-16), GetWindowLong(h,-20)); }
  public static List<string> List(int pid) {
    var res = new List<string>();
    EnumWindows((h, l) => { int p; GetWindowThreadProcessId(h, out p); if (p == pid) { res.Add(D(h, "")); EnumChildWindows(h, (c, l2) => { res.Add(D(c, "   child ")); return true; }, IntPtr.Zero); } return true; }, IntPtr.Zero);
    return res;
  }
}
"@ }
[EW]::List($ProcId)
