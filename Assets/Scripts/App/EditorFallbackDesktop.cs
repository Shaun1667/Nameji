using System.Collections.Generic;
using Shimeji.Core;
using UnityEngine;

namespace Shimeji.App
{
    /// <summary>
    /// 에디터 Game 뷰용 가짜 데스크톱. Game 뷰 전체를 모니터 1개로 보고, 가짜 창 2개를 둔다.
    /// (투명 창/클릭 통과/실제 창 감지는 Windows 빌드에서만 확인 가능)
    /// </summary>
    public sealed class EditorFallbackDesktop : IDesktopApi, IPointerApi
    {
        public const float TaskbarHeight = 40f;
        public bool FakeWindows = true;
        /// <summary>가짜 창 하나를 천천히 좌우로 움직여 창 추종을 확인한다.</summary>
        public bool AnimateWindow = true;

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            float w = Screen.width, h = Screen.height;
            return new[] { new MonitorInfo(new RectPx(0, 0, w, h), new RectPx(0, 0, w, h - TaskbarHeight), true) };
        }

        public IReadOnlyList<WindowInfo> GetWindows()
        {
            var list = new List<WindowInfo>();
            if (!FakeWindows) return list;
            float w = Screen.width, h = Screen.height;
            float sway = AnimateWindow ? Mathf.Sin(Time.realtimeSinceStartup * 0.2f) * w * 0.05f : 0f;
            list.Add(new WindowInfo
            {
                Hwnd = 101, ClassName = "FakeWindow", Title = "Fake A",
                Bounds = new RectPx(w * 0.08f + sway, h * 0.45f, w * 0.38f + sway, h * 0.8f),
            });
            list.Add(new WindowInfo
            {
                Hwnd = 102, ClassName = "FakeWindow", Title = "Fake B",
                Bounds = new RectPx(w * 0.55f, h * 0.25f, w * 0.85f, h * 0.6f),
            });
            return list;
        }

        public Vec2 GetCursorPosition()
        {
            var m = Input.mousePosition;
            return new Vec2(m.x, Screen.height - m.y);
        }

        public bool IsLeftButtonDown() => Input.GetMouseButton(0);
        public bool IsRightButtonDown() => Input.GetMouseButton(1);
    }
}
