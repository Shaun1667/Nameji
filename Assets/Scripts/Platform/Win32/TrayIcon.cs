using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Shimeji.Core;

namespace Shimeji.Platform.Win32
{
    public sealed class TrayMenuItem
    {
        public int Id;
        public string Text;
        public bool Checked;
        public bool Enabled = true;
        public bool Separator;
        public List<TrayMenuItem> Children;

        public static TrayMenuItem Sep() => new TrayMenuItem { Separator = true };
        public static TrayMenuItem Item(int id, string text, bool check = false, bool enabled = true) =>
            new TrayMenuItem { Id = id, Text = text, Checked = check, Enabled = enabled };
        public static TrayMenuItem Sub(string text, List<TrayMenuItem> children) =>
            new TrayMenuItem { Text = text, Children = children };
    }

    /// <summary>
    /// Shell_NotifyIcon 트레이 아이콘. 전용 스레드에서 숨은 창 + 메시지 루프를 돌리고,
    /// 메뉴 선택은 큐로 메인 스레드에 전달한다 (Unity 메인 스레드를 막지 않음).
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        const int WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_NULL = 0x0000, WM_COMMAND = 0x0111;
        const int WM_APP = 0x8000, WM_TRAY = WM_APP + 1, WM_REFRESH = WM_APP + 2;
        const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
        const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
        const int NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_ERROR = 3;
        const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8, MF_GRAYED = 0x1, MF_POPUP = 0x10;
        const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(int msg, ref NOTIFYICONDATAW data);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr ExtractIconW(IntPtr hInst, string path, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
        [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string text);
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out NativeMethods.POINT p);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
        [DllImport("user32.dll")] static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        static TrayIcon _instance; // WndProc가 정적이어야 하므로(IL2CPP) 단일 인스턴스
        static readonly WndProc WndProcDelegate = StaticWndProc;

        readonly string _tooltip;
        readonly string _iconPath;
        readonly ConcurrentQueue<int> _commands = new ConcurrentQueue<int>();
        volatile List<TrayMenuItem> _menu = new List<TrayMenuItem>();
        Thread _thread;
        IntPtr _hwnd;
        IntPtr _hIcon;
        uint _taskbarCreatedMsg;
        volatile bool _ready;

        public bool IsRunning => _ready;

        public TrayIcon(string tooltip, string iconPath)
        {
            _tooltip = tooltip;
            _iconPath = iconPath;
        }

        public void Start()
        {
            if (_instance != null) { Log.Warn("TrayIcon already running"); return; }
            _instance = this;
            _thread = new Thread(ThreadMain) { IsBackground = true, Name = "TrayIcon" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>메인 스레드에서 메뉴 내용을 갱신 (다음에 메뉴를 열 때 반영).</summary>
        public void SetMenu(List<TrayMenuItem> items) => _menu = items ?? new List<TrayMenuItem>();

        public bool TryDequeueCommand(out int id) => _commands.TryDequeue(out id);

        public void ShowBalloon(string title, string text, bool error)
        {
            if (!_ready) return;
            try
            {
                var data = MakeData(NIF_INFO);
                data.szInfoTitle = Truncate(title, 63);
                data.szInfo = Truncate(text, 255);
                data.dwInfoFlags = error ? NIIF_ERROR : NIIF_INFO;
                Shell_NotifyIconW(NIM_MODIFY, ref data);
            }
            catch (Exception e) { Log.Warn("Tray balloon failed: " + e.Message); }
        }

        static string Truncate(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));

        NOTIFYICONDATAW MakeData(int flags)
        {
            return new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = flags,
                uCallbackMessage = WM_TRAY,
                hIcon = _hIcon,
                szTip = Truncate(_tooltip, 127),
                szInfo = "",
                szInfoTitle = "",
            };
        }

        void ThreadMain()
        {
            try
            {
                var hInst = GetModuleHandleW(null);
                var wc = new WNDCLASSEXW
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
                    hInstance = hInst,
                    lpszClassName = "ShimejiTrayWindow",
                };
                RegisterClassExW(ref wc);
                _hwnd = CreateWindowExW(0, "ShimejiTrayWindow", "ShimejiTray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero) { Log.Error("Tray window creation failed: " + Marshal.GetLastWin32Error()); return; }
                _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

                _hIcon = string.IsNullOrEmpty(_iconPath) ? IntPtr.Zero : ExtractIconW(hInst, _iconPath, 0);
                if (_hIcon == IntPtr.Zero || _hIcon == new IntPtr(1)) _hIcon = LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
                AddIcon();
                _ready = true;
                Log.Info("Tray icon started");

                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            catch (Exception e)
            {
                Log.Error("Tray thread failed: " + e);
            }
            finally
            {
                _ready = false;
            }
        }

        void AddIcon()
        {
            var data = MakeData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            if (!Shell_NotifyIconW(NIM_ADD, ref data)) Log.Warn("Shell_NotifyIcon(NIM_ADD) failed");
        }

        [MonoPInvokeCallback(typeof(WndProc))]
        static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                var self = _instance;
                if (self != null) return self.InstanceWndProc(hwnd, msg, wParam, lParam);
            }
            catch (Exception e)
            {
                Log.Error("Tray WndProc error: " + e);
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        IntPtr InstanceWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAY)
            {
                int ev = (int)(lParam.ToInt64() & 0xFFFF);
                if (ev == WM_RBUTTONUP || ev == WM_LBUTTONUP || ev == WM_CONTEXTMENU) ShowMenu(hwnd);
                return IntPtr.Zero;
            }
            if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
            {
                AddIcon(); // 탐색기 재시작 시 아이콘 복구
                return IntPtr.Zero;
            }
            if (msg == WM_CLOSE)
            {
                var data = MakeData(0);
                Shell_NotifyIconW(NIM_DELETE, ref data);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            if (msg == WM_DESTROY)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        void ShowMenu(IntPtr hwnd)
        {
            var menu = BuildMenu(_menu);
            try
            {
                GetCursorPos(out var p);
                SetForegroundWindow(hwnd); // 메뉴 바깥 클릭 시 닫히도록 필요
                int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, p.X, p.Y, hwnd, IntPtr.Zero);
                PostMessageW(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                if (cmd > 0) _commands.Enqueue(cmd);
            }
            finally
            {
                DestroyMenu(menu);
            }
        }

        static IntPtr BuildMenu(List<TrayMenuItem> items)
        {
            var menu = CreatePopupMenu();
            foreach (var it in items)
            {
                if (it.Separator) { AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null); continue; }
                if (it.Children != null)
                {
                    var sub = BuildMenu(it.Children);
                    AppendMenuW(menu, MF_POPUP | (it.Enabled ? 0 : MF_GRAYED), sub, it.Text);
                    continue;
                }
                uint flags = MF_STRING | (it.Checked ? MF_CHECKED : 0) | (it.Enabled ? 0 : MF_GRAYED);
                AppendMenuW(menu, flags, new IntPtr(it.Id), it.Text);
            }
            return menu;
        }

        public void Dispose()
        {
            try
            {
                if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _thread?.Join(1000);
            }
            catch (Exception e) { Log.Warn("Tray dispose: " + e.Message); }
            if (_instance == this) _instance = null;
        }
    }
}
