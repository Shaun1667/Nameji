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
    /// <summary>투명 창 구현 방식 (환경에 따라 동작이 달라 실험/폴백용으로 선택 가능).</summary>
    public enum OverlayMode
    {
        /// <summary>DWM 프레임 확장 + 클릭 통과 시에만 WS_EX_LAYERED|WS_EX_TRANSPARENT.</summary>
        DwmToggleLayered = 0,
        /// <summary>DWM + 항상 WS_EX_LAYERED (SetLayeredWindowAttributes 호출 안 함).</summary>
        DwmLayered = 1,
        /// <summary>DWM + WS_EX_LAYERED + LWA_ALPHA 255.</summary>
        DwmLayeredAlpha = 2,
        /// <summary>진단용: 테두리 없는 팝업만 (투명/클릭통과 없음).</summary>
        PlainPopup = 3,
        /// <summary>DWM 프레임 확장만, 클릭 통과는 WS_EX_TRANSPARENT 단독.</summary>
        DwmOnly = 4,
    }

    public sealed class OverlayWindow : IOverlayWindow
    {
        public OverlayMode Mode { get; set; } = OverlayMode.DwmToggleLayered;
        /// <summary>스타일 변경 전 창을 숨겼다 다시 보일지 (작업표시줄 버튼 제거용).</summary>
        public bool HideWhileRestyling { get; set; } = true;
        /// <summary>GWL_STYLE을 WS_POPUP으로 바꿀지 (-popupwindow 실행 인자로 대체 가능).</summary>
        public bool ChangeStyle { get; set; } = true;

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
                if (HideWhileRestyling) ShowWindow(Hwnd, SW_HIDE);
                if (ChangeStyle) SetWindowLong(Hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);
                uint ex = WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                if (Mode == OverlayMode.DwmLayered || Mode == OverlayMode.DwmLayeredAlpha) ex |= WS_EX_LAYERED;
                SetWindowLong(Hwnd, GWL_EXSTYLE, ex);
                if (Mode == OverlayMode.DwmLayeredAlpha) SetLayeredWindowAttributes(Hwnd, 0, 255, LWA_ALPHA);
                if (Mode != OverlayMode.PlainPopup)
                {
                    var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    int hr = DwmExtendFrameIntoClientArea(Hwnd, ref margins);
                    if (hr != 0) Log.Warn($"DwmExtendFrameIntoClientArea failed: 0x{hr:X8}");
                }
                Topmost = topmost;
                Bounds = bounds;
                SetWindowPos(Hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST,
                    (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height,
                    SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                if (HideWhileRestyling) ShowWindow(Hwnd, SW_SHOWNA);
                ClickThrough = false;
                SetClickThrough(true);
                Log.Info($"Overlay applied: mode={Mode} hwnd=0x{Hwnd.ToInt64():X} bounds={bounds} exstyle=0x{GetWindowLong(Hwnd, GWL_EXSTYLE):X8}");
                return true;
            }
            catch (Exception e)
            {
                Log.Error("OverlayWindow.MakeOverlay failed: " + e);
                return false;
            }
        }

        /// <summary>
        /// ULW 백엔드 사용 시 Unity 플레이어 창은 렌더 타깃이 아니므로 보이지 않게 한다.
        /// 숨기면(SW_HIDE) Unity가 렌더링을 멈추므로, "표시 상태"는 유지하되 완전 투명(LWA_ALPHA 0) + 클릭 통과 + 작업표시줄 제외로 만든다.
        /// </summary>
        public void HideUnityWindow(RectPx at)
        {
            if (!IsValid && !Attach()) return;
            try
            {
                SetWindowLong(Hwnd, GWL_STYLE, WS_POPUP | WS_VISIBLE);
                SetWindowLong(Hwnd, GWL_EXSTYLE, WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                SetLayeredWindowAttributes(Hwnd, 0, 0, LWA_ALPHA);
                SetWindowPos(Hwnd, HWND_NOTOPMOST, (int)at.Left, (int)at.Top, 64, 64, SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
                ClickThrough = true;
                Log.Info($"Unity window made invisible: hwnd=0x{Hwnd.ToInt64():X} exstyle=0x{GetWindowLong(Hwnd, GWL_EXSTYLE):X8}");
            }
            catch (Exception e) { Log.Warn("HideUnityWindow failed: " + e.Message); }
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
                if (Mode == OverlayMode.PlainPopup) return;
                if (Mode == OverlayMode.DwmOnly)
                    ex = enabled ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
                else if (Mode == OverlayMode.DwmToggleLayered)
                    ex = enabled ? ex | WS_EX_TRANSPARENT | WS_EX_LAYERED : ex & ~(WS_EX_TRANSPARENT | WS_EX_LAYERED);
                else
                    ex = (enabled ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT) | WS_EX_LAYERED;
                SetWindowLong(Hwnd, GWL_EXSTYLE, ex);
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

        /// <summary>
        /// 전체화면 앱(게임/영상/프레젠테이션)이 앞에 있는지.
        /// SHQueryUserNotificationState의 QUNS_BUSY는 우리 오버레이 자신에도 반응하므로 쓰지 않고,
        /// D3D 전체화면/프레젠테이션 모드 + "전경 창이 모니터 전체를 덮는가"를 직접 검사한다.
        /// </summary>
        /// <summary>마지막으로 전체화면이라고 판단한 이유 (로그용).</summary>
        public static string LastFullscreenReason { get; private set; } = "";

        public static bool IsFullscreenAppActive(IntPtr selfHwnd)
        {
            try
            {
                if (SHQueryUserNotificationState(out int state) == 0 && (state == 3 || state == 4))
                {
                    LastFullscreenReason = "notification state " + state;
                    return true;
                }

                var fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == selfHwnd) return false;
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == GetCurrentProcessId()) return false;
                var sb = new StringBuilder(64);
                GetClassNameW(fg, sb, sb.Capacity);
                var cls = sb.ToString();
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
                if (!GetWindowRect(fg, out var r)) return false;
                var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
                if (mon == IntPtr.Zero || !GetMonitorInfoW(mon, ref info)) return false;
                var m = info.rcMonitor;
                bool covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
                if (covers)
                {
                    var title = new StringBuilder(128);
                    GetWindowTextW(fg, title, title.Capacity);
                    LastFullscreenReason = $"foreground '{cls}' \"{title}\" pid={pid} rect=({r.Left},{r.Top},{r.Right},{r.Bottom})";
                }
                return covers;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 오버레이 영역: 가상 데스크톱에서 위/아래 1px을 뺀다.
        /// 모니터를 완전히 덮는 최상단 창은 셸이 "전체화면 앱"으로 판정해 알림 억제/작업표시줄 뒤로 숨기기 등을 하므로 피한다.
        /// </summary>
        public static RectPx OverlayRectFor(RectPx virtualBounds) =>
            new RectPx(virtualBounds.Left, virtualBounds.Top + 1, virtualBounds.Right, virtualBounds.Bottom - 1);

        public static long CurrentProcessId
        {
            get
            {
                try { return GetCurrentProcessId(); } catch (Exception) { return 0; }
            }
        }
    }
}
