namespace Shimeji.Core
{
    /// <summary>
    /// 좌표 변환의 단일 지점.
    /// - 데스크톱 픽셀: 가상 데스크톱 좌표 (좌상단 원점, y 아래로 증가, 음수 가능)
    /// - 월드: Unity 월드 (오버레이 중심이 원점, y 위로 증가, 1 unit = PixelsPerUnit px)
    /// - 오버레이 스크린: Unity Screen 좌표 (오버레이 창 좌하단 원점)
    /// </summary>
    public sealed class ScreenSpace
    {
        public const float DefaultPixelsPerUnit = 100f;

        public float PixelsPerUnit { get; }

        /// <summary>오버레이 창이 덮는 가상 데스크톱 영역 (데스크톱 픽셀).</summary>
        public RectPx Overlay { get; private set; }

        public ScreenSpace(RectPx overlay, float pixelsPerUnit = DefaultPixelsPerUnit)
        {
            PixelsPerUnit = pixelsPerUnit;
            Overlay = overlay;
        }

        public void SetOverlay(RectPx overlay) => Overlay = overlay;

        /// <summary>정사영 카메라 orthographicSize (화면 높이의 절반, 월드 단위).</summary>
        public float OrthographicSize => Overlay.Height * 0.5f / PixelsPerUnit;

        public Vec2 DesktopToWorld(Vec2 px)
        {
            var c = Overlay.Center;
            return new Vec2((px.X - c.X) / PixelsPerUnit, -(px.Y - c.Y) / PixelsPerUnit);
        }

        public Vec2 WorldToDesktop(Vec2 w)
        {
            var c = Overlay.Center;
            return new Vec2(w.X * PixelsPerUnit + c.X, -w.Y * PixelsPerUnit + c.Y);
        }

        public Vec2 DesktopToOverlayScreen(Vec2 px) =>
            new Vec2(px.X - Overlay.Left, Overlay.Height - (px.Y - Overlay.Top));

        public Vec2 OverlayScreenToDesktop(Vec2 s) =>
            new Vec2(s.X + Overlay.Left, Overlay.Top + (Overlay.Height - s.Y));

        public float PixelsToWorld(float px) => px / PixelsPerUnit;
        public float WorldToPixels(float w) => w * PixelsPerUnit;
    }
}
