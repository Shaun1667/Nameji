using System.Collections.Generic;
using Shimeji.Core;
using UnityEngine;

namespace Shimeji.App.UI
{
    /// <summary>표면(바닥/창 윗면/벽/천장)과 캐릭터 박스를 선으로 그린다. 카메라에 붙인다.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class DebugSurfaceRenderer : MonoBehaviour
    {
        public bool ShowSurfaces;
        public bool ShowFakeWindows;
        public DesktopWorld World;
        public ScreenSpace Space;
        public readonly List<RectPx> CharacterRects = new List<RectPx>();

        Material _mat;
        Camera _cam;

        // Camera.onPostRender는 구독 순서대로 호출된다. Awake에서 구독해 LayeredPresenter(나중에 구독)의 읽기 전에 그린다.
        void Awake()
        {
            _cam = GetComponent<Camera>();
            Camera.onPostRender += Draw;
        }

        void Draw(Camera cam)
        {
            if (cam != _cam) return;
            if ((!ShowSurfaces && !ShowFakeWindows) || World == null || Space == null) return;
            if (_mat == null)
            {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) return;
                _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                _mat.SetInt("_ZWrite", 0);
                _mat.SetInt("_Cull", 0);
            }
            _mat.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            GL.Begin(GL.LINES);

            if (ShowFakeWindows)
                foreach (var w in World.Windows) Rect(w.Bounds, new Color(0.6f, 0.6f, 0.6f, 1f));

            if (ShowSurfaces)
            {
                foreach (var s in World.Surfaces)
                {
                    Color c;
                    switch (s.Type)
                    {
                        case SurfaceType.Floor: c = Color.green; break;
                        case SurfaceType.WindowTop: c = Color.cyan; break;
                        case SurfaceType.Ceiling: c = Color.magenta; break;
                        default: c = Color.yellow; break;
                    }
                    // 두 줄로 그려서 잘 보이게
                    for (int k = 0; k < 2; k++)
                    {
                        if (s.IsHorizontal) Line(new Vec2(s.Min, s.Fixed + k), new Vec2(s.Max, s.Fixed + k), c);
                        else Line(new Vec2(s.Fixed + k, s.Min), new Vec2(s.Fixed + k, s.Max), c);
                    }
                }
                foreach (var r in CharacterRects) Rect(r, Color.white);
            }

            GL.End();
            GL.PopMatrix();
        }

        void Rect(RectPx r, Color c)
        {
            Line(new Vec2(r.Left, r.Top), new Vec2(r.Right, r.Top), c);
            Line(new Vec2(r.Right, r.Top), new Vec2(r.Right, r.Bottom), c);
            Line(new Vec2(r.Right, r.Bottom), new Vec2(r.Left, r.Bottom), c);
            Line(new Vec2(r.Left, r.Bottom), new Vec2(r.Left, r.Top), c);
        }

        void Line(Vec2 a, Vec2 b, Color c)
        {
            var pa = Space.DesktopToOverlayScreen(a);
            var pb = Space.DesktopToOverlayScreen(b);
            GL.Color(c);
            GL.Vertex3(pa.X, pa.Y, 0);
            GL.Vertex3(pb.X, pb.Y, 0);
        }

        void OnDestroy()
        {
            Camera.onPostRender -= Draw;
            if (_mat != null) DestroyImmediate(_mat);
        }
    }
}
