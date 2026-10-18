using System;
using System.Runtime.InteropServices;
using System.Threading;
using Shimeji.Core;

namespace Shimeji.Platform.Win32
{
    /// <summary>
    /// UpdateLayeredWindow(ULW_ALPHA) 기반 투명 오버레이 창.
    /// Unity 창의 스왑체인을 거치지 않고, 렌더 결과(프리멀티플 BGRA)를 DIB에 써서 per-pixel alpha로 올린다.
    ///  - DXGI flip/bitblt 모델, "창 모드 게임 최적화" 등 드라이버/OS 설정과 무관하게 동작
    ///  - 알파 0 픽셀은 OS가 자동으로 클릭을 통과시킨다 (+ WS_EX_TRANSPARENT 토글로 완전 통과)
    /// 창은 전용 스레드에서 만들고 메시지 루프를 돌린다. Present/스타일 변경은 메인 스레드에서 호출해도 된다.
    /// </summary>
    public sealed class LayeredOverlayWindow : IOverlayWindow, IDisposable
    {
        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        const int GWL_EXSTYLE = -20;
        const uint ULW_ALPHA = 2;
        const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
        const uint WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3;

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [StructLayout(LayoutKind.Sequential)]
        struct UPDATELAYEREDWINDOWINFO
        {
            public int cbSize;
            public IntPtr hdcDst;
            public IntPtr pptDst;
            public IntPtr psize;
            public IntPtr hdcSrc;
            public IntPtr pptSrc;
            public uint crKey;
            public IntPtr pblend;
            public uint dwFlags;
            public IntPtr prcDirty;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public int ptX, ptY;
        }

        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UPDATELAYEREDWINDOWINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        static readonly WndProc WndProcDelegate = StaticWndProc;
        static bool _classRegistered;

        Thread _thread;
        readonly ManualResetEventSlim _created = new ManualResetEventSlim(false);
        IntPtr _memDc, _dib, _oldBmp;
        readonly object _gate = new object();

        public IntPtr Hwnd { get; private set; }
        public bool IsValid => Hwnd != IntPtr.Zero && _dib != IntPtr.Zero;
        public RectPx Bounds { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>DIB 픽셀 (BGRA 프리멀티플, top-down, stride = Width*4).</summary>
        public IntPtr Bits { get; private set; }
        public bool ClickThrough { get; private set; }
        public bool Topmost { get; private set; } = true;
        public int UploadErrors { get; private set; }

        [MonoPInvokeCallback(typeof(WndProc))]
        static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_MOUSEACTIVATE) return new IntPtr(MA_NOACTIVATE); // 클릭해도 포커스를 뺏지 않음
            if (msg == WM_CLOSE) { DestroyWindow(hwnd); return IntPtr.Zero; }
            if (msg == WM_DESTROY) { PostQuitMessage(0); return IntPtr.Zero; }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        public bool Create(RectPx bounds, bool topmost)
        {
            Topmost = topmost;
            Bounds = bounds;
            _thread = new Thread(() => ThreadMain(bounds, topmost)) { IsBackground = true, Name = "ShimejiOverlayWindow" };
            _thread.Start();
            if (!_created.Wait(3000) || Hwnd == IntPtr.Zero)
            {
                Log.Error("LayeredOverlayWindow: window creation failed");
                return false;
            }
            if (!AllocateSurface((int)bounds.Width, (int)bounds.Height)) return false;
            ClickThrough = true;
            Present(0, 0, Width, Height);
            SetVisible(true);
            Log.Info($"Layered overlay created: hwnd=0x{Hwnd.ToInt64():X} bounds={bounds}");
            return true;
        }

        void ThreadMain(RectPx b, bool topmost)
        {
            try
            {
                var hInst = GetModuleHandleW(null);
                if (!_classRegistered)
                {
                    var wc = new WNDCLASSEXW
                    {
                        cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
                        hInstance = hInst,
                        lpszClassName = "ShimejiLayeredOverlay",
                    };
                    RegisterClassExW(ref wc);
                    _classRegistered = true;
                }
                uint ex = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | (topmost ? WS_EX_TOPMOST : 0);
                Hwnd = CreateWindowExW(ex, "ShimejiLayeredOverlay", "DesktopMascot", WS_POPUP,
                    (int)b.Left, (int)b.Top, (int)b.Width, (int)b.Height, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
                if (Hwnd == IntPtr.Zero) Log.Error("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            }
            catch (Exception e)
            {
                Log.Error("overlay thread: " + e);
            }
            finally
            {
                _created.Set();
            }
            if (Hwnd == IntPtr.Zero) return;
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        bool AllocateSurface(int w, int h)
        {
            lock (_gate)
            {
                FreeSurface();
                var screen = GetDC(IntPtr.Zero);
                _memDc = CreateCompatibleDC(screen);
                ReleaseDC(IntPtr.Zero, screen);
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                };
                _dib = CreateDIBSection(_memDc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
                if (_dib == IntPtr.Zero)
                {
                    Log.Error("CreateDIBSection failed");
                    return false;
                }
                _oldBmp = SelectObject(_memDc, _dib);
                Bits = bits;
                Width = w;
                Height = h;
                return true; // CreateDIBSection 메모리는 0(투명)으로 초기화된다
            }
        }

        void FreeSurface()
        {
            if (_memDc != IntPtr.Zero)
            {
                if (_oldBmp != IntPtr.Zero) SelectObject(_memDc, _oldBmp);
                DeleteDC(_memDc);
            }
            if (_dib != IntPtr.Zero) DeleteObject(_dib);
            _memDc = _dib = _oldBmp = IntPtr.Zero;
            Bits = IntPtr.Zero;
        }

        /// <summary>DIB의 (x, y, w, h) 영역(창 좌표, 위가 0)을 화면에 반영.</summary>
        public void Present(int x, int y, int w, int h)
        {
            if (!IsValid || w <= 0 || h <= 0) return;
            lock (_gate)
            {
                var ptDst = Marshal.AllocHGlobal(8);
                var size = Marshal.AllocHGlobal(8);
                var ptSrc = Marshal.AllocHGlobal(8);
                var blend = Marshal.AllocHGlobal(4);
                var dirty = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.StructureToPtr(new POINT { X = (int)Bounds.Left, Y = (int)Bounds.Top }, ptDst, false);
                    Marshal.StructureToPtr(new SIZE { CX = Width, CY = Height }, size, false);
                    Marshal.StructureToPtr(new POINT(), ptSrc, false);
                    Marshal.StructureToPtr(new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA }, blend, false);
                    Marshal.StructureToPtr(new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, dirty, false);
                    var info = new UPDATELAYEREDWINDOWINFO
                    {
                        cbSize = Marshal.SizeOf<UPDATELAYEREDWINDOWINFO>(),
                        pptDst = ptDst,
                        psize = size,
                        hdcSrc = _memDc,
                        pptSrc = ptSrc,
                        pblend = blend,
                        dwFlags = ULW_ALPHA,
                        prcDirty = dirty,
                    };
                    if (!UpdateLayeredWindowIndirect(Hwnd, ref info))
                    {
                        UploadErrors++;
                        if (UploadErrors <= 3) Log.Warn("UpdateLayeredWindowIndirect failed: " + Marshal.GetLastWin32Error());
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptDst); Marshal.FreeHGlobal(size); Marshal.FreeHGlobal(ptSrc);
                    Marshal.FreeHGlobal(blend); Marshal.FreeHGlobal(dirty);
                }
            }
        }

        public void SetBounds(RectPx bounds)
        {
            if (Hwnd == IntPtr.Zero) return;
            Bounds = bounds;
            AllocateSurface((int)bounds.Width, (int)bounds.Height);
            SetWindowPos(Hwnd, Topmost ? new IntPtr(-1) : new IntPtr(-2), (int)bounds.Left, (int)bounds.Top, (int)bounds.Width, (int)bounds.Height, 0x0010);
            Present(0, 0, Width, Height);
        }

        public void SetClickThrough(bool enabled)
        {
            if (Hwnd == IntPtr.Zero || enabled == ClickThrough) return;
            long ex = GetWindowLongPtr(Hwnd, GWL_EXSTYLE).ToInt64();
            ex = enabled ? ex | WS_EX_TRANSPARENT : ex & ~(long)WS_EX_TRANSPARENT;
            SetWindowLongPtr(Hwnd, GWL_EXSTYLE, new IntPtr(ex));
            ClickThrough = enabled;
        }

        public uint ExStyle => Hwnd == IntPtr.Zero ? 0 : (uint)GetWindowLongPtr(Hwnd, GWL_EXSTYLE).ToInt64();

        public void SetTopmost(bool topmost)
        {
            if (Hwnd == IntPtr.Zero) return;
            Topmost = topmost;
            SetWindowPos(Hwnd, topmost ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        public void SetVisible(bool visible)
        {
            if (Hwnd != IntPtr.Zero) ShowWindow(Hwnd, visible ? 8 /*SW_SHOWNA*/ : 0 /*SW_HIDE*/);
        }

        public void Dispose()
        {
            try
            {
                if (Hwnd != IntPtr.Zero) PostMessageW(Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _thread?.Join(1000);
            }
            catch (Exception) { /* 종료 중 */ }
            lock (_gate) FreeSurface();
            Hwnd = IntPtr.Zero;
        }
    }
}
