using System;
using Shimeji.Core;

namespace Shimeji.Platform.Win32
{
    /// <summary>오버레이 창 백엔드 공통 인터페이스 (UpdateLayeredWindow 방식 / DWM 방식).</summary>
    public interface IOverlayWindow
    {
        IntPtr Hwnd { get; }
        bool ClickThrough { get; }
        uint ExStyle { get; }
        void SetClickThrough(bool enabled);
        void SetTopmost(bool topmost);
        void SetVisible(bool visible);
        void SetBounds(RectPx bounds);
    }
}
