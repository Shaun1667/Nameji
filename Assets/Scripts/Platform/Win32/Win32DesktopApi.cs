using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Shimeji.Core;
using static Shimeji.Platform.Win32.NativeMethods;

namespace Shimeji.Platform.Win32
{
    /// <summary>
    /// 모니터/창 정보를 읽기 전용으로 수집한다. 다른 창을 조작하지 않는다.
    /// 메인 스레드에서만 호출할 것(콜백이 정적 버퍼를 쓴다).
    /// </summary>
    public sealed class Win32DesktopApi : IDesktopApi
    {
        static readonly List<IntPtr> HwndBuffer = new List<IntPtr>(256);
        static readonly List<IntPtr> MonitorBuffer = new List<IntPtr>(8);
        static readonly EnumWindowsProc EnumWindowsCallback = OnEnumWindow;
        static readonly MonitorEnumProc EnumMonitorCallback = OnEnumMonitor;
        readonly StringBuilder _sb = new StringBuilder(256);
        bool _warnedWindows, _warnedMonitors;

        [MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool OnEnumWindow(IntPtr hwnd, IntPtr lParam)
        {
            HwndBuffer.Add(hwnd);
            return true;
        }

        [MonoPInvokeCallback(typeof(MonitorEnumProc))]
        static bool OnEnumMonitor(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
        {
            MonitorBuffer.Add(hMonitor);
            return true;
        }

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            var result = new List<MonitorInfo>();
            try
            {
                MonitorBuffer.Clear();
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, EnumMonitorCallback, IntPtr.Zero);
                foreach (var h in MonitorBuffer)
                {
                    var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    if (!GetMonitorInfoW(h, ref info)) continue;
                    float scale = 1f;
                    try
                    {
                        if (GetDpiForMonitor(h, 0, out var dx, out _) == 0 && dx > 0) scale = dx / 96f;
                    }
                    catch (Exception) { /* Windows 8.1 미만 */ }
                    result.Add(new MonitorInfo(ToRect(info.rcMonitor), ToRect(info.rcWork), (info.dwFlags & MONITORINFOF_PRIMARY) != 0, scale));
                }
            }
            catch (Exception e)
            {
                if (!_warnedMonitors) { Log.Error("EnumDisplayMonitors failed: " + e); _warnedMonitors = true; }
            }
            return result;
        }

        public IReadOnlyList<WindowInfo> GetWindows()
        {
            var result = new List<WindowInfo>();
            try
            {
                HwndBuffer.Clear();
                EnumWindows(EnumWindowsCallback, IntPtr.Zero);
                foreach (var h in HwndBuffer)
                {
                    if (!IsWindowVisible(h)) continue; // 대부분 여기서 걸러져 비용이 작다
                    var w = new WindowInfo { Hwnd = h.ToInt64(), Visible = true };
                    w.Minimized = IsIconic(h);
                    w.Maximized = IsZoomed(h);
                    uint ex = GetWindowLong(h, GWL_EXSTYLE);
                    w.ToolWindow = (ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0;
                    w.Transparent = (ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0;
                    if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0) w.Cloaked = cloaked != 0;
                    if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0)
                        w.Bounds = ToRect(r);
                    else if (GetWindowRect(h, out r))
                        w.Bounds = ToRect(r);
                    _sb.Clear();
                    GetClassNameW(h, _sb, _sb.Capacity);
                    w.ClassName = _sb.ToString();
                    _sb.Clear();
                    GetWindowTextW(h, _sb, _sb.Capacity);
                    w.Title = _sb.ToString();
                    GetWindowThreadProcessId(h, out uint pid);
                    w.ProcessId = (int)pid;
                    result.Add(w);
                }
            }
            catch (Exception e)
            {
                if (!_warnedWindows) { Log.Error("EnumWindows failed: " + e); _warnedWindows = true; }
            }
            return result;
        }

        static RectPx ToRect(RECT r) => new RectPx(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>GetCursorPos + GetAsyncKeyState 폴링 (전역 훅을 쓰지 않는다).</summary>
    public sealed class Win32PointerApi : IPointerApi
    {
        public Vec2 GetCursorPosition()
        {
            try
            {
                return GetCursorPos(out var p) ? new Vec2(p.X, p.Y) : Vec2.Zero;
            }
            catch (Exception) { return Vec2.Zero; }
        }

        static bool Swapped
        {
            get
            {
                try { return GetSystemMetrics(SM_SWAPBUTTON) != 0; } catch (Exception) { return false; }
            }
        }

        public bool IsLeftButtonDown() => KeyDown(Swapped ? VK_RBUTTON : VK_LBUTTON);
        public bool IsRightButtonDown() => KeyDown(Swapped ? VK_LBUTTON : VK_RBUTTON);

        static bool KeyDown(int vk)
        {
            try { return (GetAsyncKeyState(vk) & 0x8000) != 0; } catch (Exception) { return false; }
        }
    }
}
