using System.Collections.Generic;

namespace Shimeji.Core
{
    /// <summary>
    /// 표면 종류. 벽은 "캐릭터 기준으로 벽이 어느 쪽에 있는가"로 구분한다.
    /// WallLeft = 캐릭터의 왼쪽에 있는 벽(오른쪽을 향한 면: 화면 왼쪽 끝, 창의 오른쪽 옆면).
    /// WallRight = 캐릭터의 오른쪽에 있는 벽(화면 오른쪽 끝, 창의 왼쪽 옆면).
    /// </summary>
    public enum SurfaceType
    {
        Floor,
        WindowTop,
        WallLeft,
        WallRight,
        Ceiling,
    }

    /// <summary>
    /// 지형 표면(선분). 수평이면 Y 고정 + [Min, Max]는 X 구간, 수직이면 X 고정 + [Min, Max]는 Y 구간.
    /// </summary>
    public sealed class Surface
    {
        public SurfaceType Type;
        /// <summary>수평 표면의 Y 또는 수직 표면의 X.</summary>
        public float Fixed;
        public float Min;
        public float Max;
        /// <summary>표면 소유자. 창이면 hwnd, 모니터 작업영역이면 0.</summary>
        public long OwnerId;

        public Surface(SurfaceType type, float fixedCoord, float min, float max, long ownerId)
        {
            Type = type; Fixed = fixedCoord; Min = min; Max = max; OwnerId = ownerId;
        }

        public bool IsHorizontal => Type == SurfaceType.Floor || Type == SurfaceType.WindowTop || Type == SurfaceType.Ceiling;
        public bool IsGround => Type == SurfaceType.Floor || Type == SurfaceType.WindowTop;
        public bool IsWall => Type == SurfaceType.WallLeft || Type == SurfaceType.WallRight;
        public float Length => Max - Min;

        public bool ContainsAlong(float v, float tol = 0f) => v >= Min - tol && v <= Max + tol;

        public override string ToString() => $"{Type}@{Fixed:0} [{Min:0}..{Max:0}] owner={OwnerId}";
    }

    public sealed class MonitorInfo
    {
        public RectPx Bounds;
        public RectPx WorkArea;
        public bool Primary;
        /// <summary>DPI 배율 (1.0 = 96dpi).</summary>
        public float Scale = 1f;

        public MonitorInfo() { }
        public MonitorInfo(RectPx bounds, RectPx work, bool primary = false, float scale = 1f)
        {
            Bounds = bounds; WorkArea = work; Primary = primary; Scale = scale;
        }
    }

    /// <summary>EnumWindows 원시 결과 (필터링 전).</summary>
    public sealed class WindowInfo
    {
        public long Hwnd;
        public string ClassName = "";
        public string Title = "";
        /// <summary>그림자 제외 실제 사각형 (DWMWA_EXTENDED_FRAME_BOUNDS).</summary>
        public RectPx Bounds;
        public bool Visible = true;
        public bool Minimized;
        public bool Cloaked;
        public bool ToolWindow;
        /// <summary>WS_EX_TRANSPARENT (다른 오버레이류, 클릭 통과 창).</summary>
        public bool Transparent;
        public bool Maximized;
        public int ProcessId;
    }

    /// <summary>Win32 데스크톱 정보 읽기 전용 인터페이스. 테스트에서 가짜 구현을 주입한다.</summary>
    public interface IDesktopApi
    {
        IReadOnlyList<MonitorInfo> GetMonitors();
        /// <summary>Z-order 순서(맨 위가 먼저)의 최상위 창 목록.</summary>
        IReadOnlyList<WindowInfo> GetWindows();
    }

    /// <summary>마우스 상태 폴링 인터페이스.</summary>
    public interface IPointerApi
    {
        Vec2 GetCursorPosition();
        bool IsLeftButtonDown();
        bool IsRightButtonDown();
    }

    /// <summary>물리/행동이 바라보는 월드.</summary>
    public interface IWorld
    {
        IReadOnlyList<Surface> Surfaces { get; }
        IReadOnlyList<MonitorInfo> Monitors { get; }
        bool TryGetOwnerRect(long ownerId, out RectPx rect);
    }
}
