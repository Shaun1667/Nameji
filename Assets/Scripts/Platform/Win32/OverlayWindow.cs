using System;
using System.Text;
using Shimeji.Core;
using static Shimeji.Platform.Win32.NativeMethods;

namespace Shimeji.Platform.Win32
{
    /// <summary>
    /// Unity 플레이어 창을 테두리 없는 투명/최상단/클릭 통과 오버레이로 만든다.
    /// 모든 호출은 실패해도 예외를 던지지 않고 로그 + 기능 비활성으로 격하한다.
    /// </summary>
    public sealed class OverlayWindow
    {
        public IntPtr Hwnd { get; private set; }
        public bool IsValid => Hwnd != IntPtr.Zero;
        public bool ClickThrough { get; private set; }
        public bool Topmost { get; private set; } = true;
        public RectPx Bounds { get; private set; }

        static IntPtr _found;
        static readonly EnumWindowsProc FindCallback = OnFindWindow;

        [MonoPInvokeCallback(typeof(EnumWindowsProc))]
        static bool OnFindWindow(IntPtr hwnd, IntPtr lParam)
        {
            var sb = new StringBuilder(64);
            GetClassNameW(hwnd, sb, sb.Capacity);
            if (sb.ToString() == "UnityWndClass") { _found = hwnd; return false; }
            return true;
        }

        /// <summary>Per-Monitor V2 DPI 인식. Unity 플레이어가 이미 설정했으면 실패해도 정상.</summary>
        public static string EnsurePerMonitorDpiAwareness()
        {
            try
            {
                bool ok = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                int awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
                // 0 = unaware, 1 = system aware, 2 = per monitor aware
                return $"SetProcessDpiAwarenessContext={(ok ? "ok" : "already set")}, thread awareness={awareness}";
            }
            catch (Exception e)
            {
                return "DPI awareness query failed: " + e.Message;
            }
        }

        /// <summary>현재 스레드의 Unity 창을 찾는다.</summary>
        public bool Attach()
        {
            try
            {
                _found = IntPtr.Zero;
                EnumThreadWindows(GetCurrentThreadId(), FindCallback, IntPtr.Zero);
                Hwnd = _found != IntPtr.Zero ? _found : GetActiveWindow();
                if (Hwnd == IntPtr.Zero) Log.Error("OverlayWindow: Unity window handle not found");
                return IsValid;
            }
            catch (Exception e)
            {
                Log.Error("OverlayWindow.Attach failed: " + e);
                return false;
            }
        }

        /// <summary>스타일 적용 + 영역 설정. 시작 직후 한두 프레임 뒤에 호출할 것.</summary>
        public bool MakeOverlay(RectPx bounds, bool topmost)
        {
            if (!IsValid && !Attach()) return false;
            try
            {
                // 작업표시줄 버튼을 확실히 없애려면 스타일 변경 전 숨겼다가 다시 보인다.
                ShowWindow(Hwnd, SW_HIDE);
                SetWindowLong(Hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);
                SetWindowLong(Hwnd, GWL_EXSTYLE, WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                SetLayeredWindowAttributes(Hwnd, 0, 255, LWA_ALPHA);
                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                int hr = DwmExtendFrameIntoClientArea(Hwnd, ref margins);
                if (hr != 0) Log.Warn($"DwmExtendFrameIntoClientArea failed: 0x{hr:X8}");
                Topmost = topmost;
                Bounds = bounds;
                SetWindowPos(Hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST,
                    (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                ShowWindow(Hwnd, SW_SHOWNA);
                ClickThrough = false;
                SetClickThrough(true);
                Log.Info($"Overlay applied: hwnd=0x{Hwnd.ToInt64():X} bounds={bounds} exstyle=0x{GetWindowLong(Hwnd, GWL_EXSTYLE):X8}");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("OverlayWindow.MakeOverlay failed: " + e);
                return false;
            }
        }

        public void SetBounds(RectPx bounds)
        {
            if (!IsValid) return;
            try
            {
                Bounds = bounds;
                SetWindowPos(Hwnd, Topmost ? HWND_TOPMOST : HWND_NOTOPMOST,
                    (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height, SWP_NOACTIVATE);
            }
            catch (Exception e) { Log.Warn("SetBounds failed: " + e.Message); }
        }

        /// <summary>true면 마우스가 아래 창으로 통과(WS_EX_TRANSPARENT).</summary>
        public void SetClickThrough(bool enabled)
        {
            if (!IsValid || enabled == ClickThrough) return;
            try
            {
                uint ex = GetWindowLong(Hwnd, GWL_EXSTYLE);
                ex = enabled ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
                SetWindowLong(Hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
                ClickThrough = enabled;
            }
            catch (Exception e) { Log.Warn("SetClickThrough failed: " + e.Message); }
        }

        public void SetTopmost(bool topmost)
        {
            if (!IsValid) return;
            Topmost = topmost;
            try
            {
                SetWindowPos(Hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception e) { Log.Warn("SetTopmost failed: " + e.Message); }
        }

        /// <summary>항상 위 상태를 주기적으로 재확인 (다른 최상단 창이 앞으로 나와도 다시 위로).</summary>
        public void ReassertTopmost()
        {
            if (Topmost) SetTopmost(true);
        }

        public void SetVisible(bool visible)
        {
            if (!IsValid) return;
            try { ShowWindow(Hwnd, visible ? SW_SHOWNA : SW_HIDE); }
            catch (Exception e) { Log.Warn("SetVisible failed: " + e.Message); }
        }

        public uint ExStyle => IsValid ? GetWindowLong(Hwnd, GWL_EXSTYLE) : 0;

        /// <summary>전체화면 앱(게임/영상/프레젠테이션)이 앞에 있는지.</summary>
        public static bool IsFullscreenAppActive()
        {
            try
            {
                if (SHQueryUserNotificationState(out int state) != 0) return false;
                // 2 = QUNS_BUSY, 3 = QUNS_RUNNING_D3D_FULL_SCREEN, 4 = QUNS_PRESENTATION_MODE
                return state == 2 || state == 3 || state == 4;
            }
            catch (Exception) { return false; }
        }

        public static long CurrentProcessId
        {
            get
            {
                try { return GetCurrentProcessId(); } catch (Exception) { return 0; }
            }
        }
    }
}
