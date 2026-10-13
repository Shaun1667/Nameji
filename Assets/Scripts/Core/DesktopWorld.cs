using System;
using System.Collections.Generic;

namespace Shimeji.Core
{
    /// <summary>표면으로 쓸 수 있는 창인지 판정 (순수 함수).</summary>
    public static class WindowFilter
    {
        public const float MinWidth = 80f;
        public const float MinHeight = 40f;

        static readonly HashSet<string> ExcludedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
            "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
            "Xaml_WindowedPopupClass", "ForegroundStaging", "MultitaskingViewFrame",
            "TaskListThumbnailWnd", "SysShadow", "tooltips_class32", "#32768",
        };

        public static bool IsSurfaceCandidate(WindowInfo w, long selfHwnd, int selfProcessId)
        {
            if (w == null) return false;
            if (!w.Visible || w.Minimized || w.Cloaked || w.ToolWindow || w.Transparent) return false;
            if (w.Hwnd == selfHwnd) return false;
            if (selfProcessId != 0 && w.ProcessId == selfProcessId) return false;
            if (w.Bounds.Width < MinWidth || w.Bounds.Height < MinHeight) return false;
            if (ExcludedClasses.Contains(w.ClassName ?? "")) return false;
            if (string.IsNullOrEmpty(w.Title)) return false;
            return true;
        }

        public static List<WindowInfo> Filter(IReadOnlyList<WindowInfo> raw, long selfHwnd, int selfProcessId)
        {
            var list = new List<WindowInfo>();
            if (raw == null) return list;
            foreach (var w in raw)
                if (IsSurfaceCandidate(w, selfHwnd, selfProcessId)) list.Add(w);
            return list;
        }
    }

    /// <summary>
    /// 모니터 작업영역과 창 사각형으로부터 표면(바닥/창 윗면/벽/천장)을 구성한다.
    /// Win32에 의존하지 않으며, 입력은 IDesktopApi 또는 직접 전달한 목록이다.
    /// </summary>
    public sealed class DesktopWorld : IWorld
    {
        /// <summary>창 윗면이 작업영역 윗변에 이 거리 이하로 붙어 있으면 표면으로 쓰지 않는다(최대화 창 등).</summary>
        public float MinHeadroom = 24f;
        /// <summary>이보다 짧은 표면 조각은 버린다.</summary>
        public float MinSurfaceLength = 12f;

        readonly List<Surface> _surfaces = new List<Surface>();
        readonly List<MonitorInfo> _monitors = new List<MonitorInfo>();
        readonly List<WindowInfo> _windows = new List<WindowInfo>();
        readonly Dictionary<long, RectPx> _ownerRects = new Dictionary<long, RectPx>();
        int _signature;

        public IReadOnlyList<Surface> Surfaces => _surfaces;
        public IReadOnlyList<MonitorInfo> Monitors => _monitors;
        public IReadOnlyList<WindowInfo> Windows => _windows;
        public int Version { get; private set; }
        public RectPx VirtualBounds { get; private set; }

        public event Action Changed;

        public bool TryGetOwnerRect(long ownerId, out RectPx rect) => _ownerRects.TryGetValue(ownerId, out rect);

        /// <summary>API에서 다시 읽어 변화가 있으면 재구성. 변화가 있었으면 true.</summary>
        public bool Refresh(IDesktopApi api, long selfHwnd, int selfProcessId)
        {
            IReadOnlyList<MonitorInfo> monitors;
            IReadOnlyList<WindowInfo> raw;
            try
            {
                monitors = api.GetMonitors();
                raw = api.GetWindows();
            }
            catch (Exception e)
            {
                Log.Warn("DesktopWorld: desktop query failed: " + e.Message);
                return false;
            }
            return Rebuild(monitors, WindowFilter.Filter(raw, selfHwnd, selfProcessId));
        }

        /// <summary>필터링이 끝난 창 목록(Z-order 위→아래)으로 재구성.</summary>
        public bool Rebuild(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<WindowInfo> windows)
        {
            int sig = ComputeSignature(monitors, windows);
            if (sig == _signature && Version > 0) return false;
            _signature = sig;

            _monitors.Clear();
            if (monitors != null) _monitors.AddRange(monitors);
            _windows.Clear();
            if (windows != null) _windows.AddRange(windows);
            _ownerRects.Clear();
            foreach (var w in _windows) _ownerRects[w.Hwnd] = w.Bounds;

            VirtualBounds = default;
            for (int i = 0; i < _monitors.Count; i++)
                VirtualBounds = i == 0 ? _monitors[i].Bounds : RectPx.Union(VirtualBounds, _monitors[i].Bounds);

            _surfaces.Clear();
            BuildMonitorSurfaces();
            BuildWindowSurfaces();

            Version++;
            Changed?.Invoke();
            return true;
        }

        static int ComputeSignature(IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<WindowInfo> windows)
        {
            unchecked
            {
                int h = 17;
                if (monitors != null)
                    foreach (var m in monitors) h = h * 31 + m.Bounds.GetHashCode() * 7 + m.WorkArea.GetHashCode();
                if (windows != null)
                    foreach (var w in windows) h = h * 31 + w.Hwnd.GetHashCode() * 13 + w.Bounds.GetHashCode() + (w.Maximized ? 1 : 0);
                return h;
            }
        }

        void Add(SurfaceType type, float fixedCoord, List<Intervals.Span> spans, long owner)
        {
            foreach (var s in spans)
                if (s.Length >= MinSurfaceLength)
                    _surfaces.Add(new Surface(type, fixedCoord, s.A, s.B, owner));
        }

        static List<Intervals.Span> One(float a, float b) => new List<Intervals.Span> { new Intervals.Span(a, b) };

        void BuildMonitorSurfaces()
        {
            foreach (var m in _monitors)
            {
                var w = m.WorkArea;
                var b = m.Bounds;

                var floor = One(w.Left, w.Right);
                var ceil = One(w.Left, w.Right);
                var wallL = One(w.Top, w.Bottom);
                var wallR = One(w.Top, w.Bottom);

                foreach (var o in _monitors)
                {
                    if (ReferenceEquals(o, m)) continue;
                    var ob = o.Bounds;
                    // 아래로 이어진 모니터: 작업표시줄이 없으면 바닥이 뚫려 있다.
                    if (w.Bottom >= b.Bottom && Near(ob.Top, b.Bottom)) floor = Intervals.Subtract(floor, ob.Left, ob.Right);
                    if (w.Top <= b.Top && Near(ob.Bottom, b.Top)) ceil = Intervals.Subtract(ceil, ob.Left, ob.Right);
                    if (w.Left <= b.Left && Near(ob.Right, b.Left)) wallL = Intervals.Subtract(wallL, ob.Top, ob.Bottom);
                    if (w.Right >= b.Right && Near(ob.Left, b.Right)) wallR = Intervals.Subtract(wallR, ob.Top, ob.Bottom);
                }

                Add(SurfaceType.Floor, w.Bottom, floor, 0);
                Add(SurfaceType.Ceiling, w.Top, ceil, 0);
                Add(SurfaceType.WallLeft, w.Left, wallL, 0);
                Add(SurfaceType.WallRight, w.Right, wallR, 0);
            }
        }

        static bool Near(float a, float b) => Math.Abs(a - b) <= 1f;

        void BuildWindowSurfaces()
        {
            for (int i = 0; i < _windows.Count; i++)
            {
                var win = _windows[i];
                var r = win.Bounds;

                // --- 윗면 ---
                if (!win.Maximized)
                {
                    float y = r.Top;
                    var spans = One(r.Left, r.Right);
                    for (int j = 0; j < i; j++)
                    {
                        var o = _windows[j].Bounds;
                        if (o.Top <= y && y < o.Bottom) spans = Intervals.Subtract(spans, o.Left, o.Right);
                    }
                    var clips = new List<Intervals.Span>();
                    foreach (var m in _monitors)
                        if (y >= m.WorkArea.Top + MinHeadroom && y < m.WorkArea.Bottom)
                            clips.Add(new Intervals.Span(m.WorkArea.Left, m.WorkArea.Right));
                    Add(SurfaceType.WindowTop, y, Intervals.IntersectWithUnion(spans, clips), win.Hwnd);
                }

                if (win.Maximized) continue;

                // --- 옆면 (창 왼쪽 면 = 캐릭터 기준 오른쪽 벽) ---
                AddWindowSide(i, r.Left, SurfaceType.WallRight, r, win.Hwnd);
                AddWindowSide(i, r.Right, SurfaceType.WallLeft, r, win.Hwnd);

                // --- 아랫면 = 천장 ---
                {
                    float y = r.Bottom;
                    var spans = One(r.Left, r.Right);
                    for (int j = 0; j < i; j++)
                    {
                        var o = _windows[j].Bounds;
                        if (o.Top < y && y <= o.Bottom) spans = Intervals.Subtract(spans, o.Left, o.Right);
                    }
                    var clips = new List<Intervals.Span>();
                    foreach (var m in _monitors)
                        if (y > m.WorkArea.Top && y < m.WorkArea.Bottom - MinHeadroom)
                            clips.Add(new Intervals.Span(m.WorkArea.Left, m.WorkArea.Right));
                    Add(SurfaceType.Ceiling, y, Intervals.IntersectWithUnion(spans, clips), win.Hwnd);
                }
            }
        }

        void AddWindowSide(int index, float x, SurfaceType type, RectPx r, long owner)
        {
            var spans = One(r.Top, r.Bottom);
            for (int j = 0; j < index; j++)
            {
                var o = _windows[j].Bounds;
                bool covers = type == SurfaceType.WallRight ? (o.Left <= x && x < o.Right) : (o.Left < x && x <= o.Right);
                if (covers) spans = Intervals.Subtract(spans, o.Top, o.Bottom);
            }
            var clips = new List<Intervals.Span>();
            foreach (var m in _monitors)
                if (x > m.WorkArea.Left && x < m.WorkArea.Right)
                    clips.Add(new Intervals.Span(m.WorkArea.Top, m.WorkArea.Bottom));
            Add(type, x, Intervals.IntersectWithUnion(spans, clips), owner);
        }

        // ---------------- 질의 ----------------

        public MonitorInfo MonitorAt(Vec2 p)
        {
            foreach (var m in _monitors) if (m.Bounds.Contains(p)) return m;
            return null;
        }

        public static MonitorInfo FindMonitor(IReadOnlyList<MonitorInfo> monitors, Vec2 p)
        {
            foreach (var m in monitors) if (m.Bounds.Contains(p)) return m;
            return null;
        }

        public static MonitorInfo PrimaryOrFirst(IReadOnlyList<MonitorInfo> monitors)
        {
            foreach (var m in monitors) if (m.Primary) return m;
            return monitors.Count > 0 ? monitors[0] : null;
        }
    }
}
