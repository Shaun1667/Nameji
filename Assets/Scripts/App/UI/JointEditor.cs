using System;
using System.Collections.Generic;
using Shimeji.Characters;
using Shimeji.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Shimeji.App.UI
{
    /// <summary>
    /// 2D 이미지 캐릭터의 관절 편집 UI.
    /// 자동 감지된 관절을 점으로 보여 주고, 드래그로 옮긴 뒤 저장하면 character.json의 "joints"에 기록한다.
    /// (클릭 통과 오버레이라 EventSystem 대신 앱의 Win32 폴링 입력으로 조작한다)
    /// </summary>
    public sealed class JointEditor
    {
        public const int ToolbarTargetId = 1_000_001;
        public const int HandleTargetBase = 2_000_000;
        const float HandleRadius = 9f;

        static readonly (string a, string b)[] Links =
        {
            (SpriteJoints.Top, SpriteJoints.Neck), (SpriteJoints.Neck, SpriteJoints.Hips),
            (SpriteJoints.ShoulderA, SpriteJoints.ElbowA), (SpriteJoints.ElbowA, SpriteJoints.HandA),
            (SpriteJoints.ShoulderB, SpriteJoints.ElbowB), (SpriteJoints.ElbowB, SpriteJoints.HandB),
            (SpriteJoints.HipA, SpriteJoints.KneeA), (SpriteJoints.KneeA, SpriteJoints.FootA),
            (SpriteJoints.HipB, SpriteJoints.KneeB), (SpriteJoints.KneeB, SpriteJoints.FootB),
            (SpriteJoints.Neck, SpriteJoints.ShoulderA), (SpriteJoints.Neck, SpriteJoints.ShoulderB),
            (SpriteJoints.Hips, SpriteJoints.HipA), (SpriteJoints.Hips, SpriteJoints.HipB),
        };

        readonly Transform _canvas;
        readonly Font _font;
        readonly Camera _cam;
        readonly ContextMenuView _toolbar;
        readonly RectTransform _root;
        readonly Dictionary<string, Vector3> _model = new Dictionary<string, Vector3>(); // 관절의 모델 로컬 위치
        readonly Dictionary<string, RectTransform> _handles = new Dictionary<string, RectTransform>();
        readonly List<RectTransform> _lines = new List<RectTransform>();
        Action<Dictionary<string, Vec2>> _onSave;
        Action _onReset;
        Action _onEnd;
        string _dragging;
        ScreenSpace _space;

        public CharacterActor Actor { get; private set; }
        public bool Active => Actor != null;

        public JointEditor(Transform canvas, Font font, Camera cam)
        {
            _canvas = canvas;
            _font = font;
            _cam = cam;
            _toolbar = new ContextMenuView(canvas, font, cam);
            var go = new GameObject("JointEditor", typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            _root = (RectTransform)go.transform;
            _root.anchorMin = _root.anchorMax = Vector2.zero;
            go.SetActive(false);
        }

        Transform ModelRoot => Actor.Oriented.childCount > 0 ? Actor.Oriented.GetChild(0) : Actor.Oriented;

        /// <param name="onSave">정규화 관절 좌표(0..1)를 받아 저장</param>
        /// <param name="onReset">character.json의 joints를 지워 자동 감지로 되돌림</param>
        public void Begin(CharacterActor actor, ScreenSpace space, Action<Dictionary<string, Vec2>> onSave, Action onReset, Action onEnd)
        {
            End();
            if (actor == null || !actor.IsSprite) return;
            Actor = actor;
            _space = space;
            _onSave = onSave;
            _onReset = onReset;
            _onEnd = onEnd;
            actor.Anim.Frozen = true;
            (actor.Anim.Poser as SpritePoseApplier)?.ApplyRest();

            var info = actor.Sprite;
            _model.Clear();
            foreach (var kv in info.Rig.Joints) _model[kv.Key] = info.PixelToModel(kv.Value);

            foreach (Transform c in _root) UnityEngine.Object.Destroy(c.gameObject);
            _handles.Clear();
            _lines.Clear();
            foreach (var _ in Links) _lines.Add(MakeRect("Line", new Color(1f, 1f, 1f, 0.85f)));
            foreach (var name in SpriteJoints.All)
            {
                var color = name.EndsWith("A") ? new Color(1f, 0.45f, 0.35f) : name.EndsWith("B") ? new Color(0.35f, 0.65f, 1f) : new Color(1f, 0.85f, 0.2f);
                var h = MakeRect("Joint_" + name, color);
                h.sizeDelta = Vector2.one * HandleRadius * 1.6f;
                _handles[name] = h;
            }
            _root.gameObject.SetActive(true);
            OpenToolbar();
            Update();
        }

        RectTransform MakeRect(string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_root, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return rt;
        }

        void OpenToolbar()
        {
            var top = DesktopOf(SpriteJoints.Top);
            var pos = _space.DesktopToOverlayScreen(new Vec2(top.X + Actor.Body.Height * 0.6f, top.Y)).ToVector2();
            _toolbar.Open(ToolbarTargetId, pos, new List<ContextMenuView.Item>
            {
                new ContextMenuView.Item("관절 저장", Save),
                new ContextMenuView.Item("자동 감지로 되돌리기", () => { var r = _onReset; End(); r?.Invoke(); }),
                new ContextMenuView.Item("취소", End),
            });
        }

        public void Save()
        {
            var info = Actor.Sprite;
            var result = new Dictionary<string, Vec2>();
            foreach (var kv in _model)
            {
                var px = info.ModelToPixel(kv.Value);
                result[kv.Key] = new Vec2(px.X / info.ImageWidth, px.Y / info.ImageHeight);
            }
            var cb = _onSave;
            End();
            cb?.Invoke(result);
        }

        public void End()
        {
            if (Actor != null && Actor.Anim != null) Actor.Anim.Frozen = false;
            var endCb = Actor != null ? _onEnd : null;
            Actor = null;
            _dragging = null;
            _toolbar.Close();
            _root.gameObject.SetActive(false);
            endCb?.Invoke();
        }

        Vec2 DesktopOf(string joint)
        {
            var w = ModelRoot.TransformPoint(_model[joint]);
            return _space.WorldToDesktop(new Vec2(w.x, w.y));
        }

        /// <summary>커서 아래 편집 대상: 툴바 / 관절 점 / 없음(-1).</summary>
        public int HitTest(Vec2 cursor, Vector2 overlayPt)
        {
            if (!Active) return -1;
            if (_toolbar.Contains(overlayPt)) return ToolbarTargetId;
            int best = -1;
            float bestD = HandleRadius + 3f;
            for (int i = 0; i < SpriteJoints.All.Length; i++)
            {
                float d = (DesktopOf(SpriteJoints.All[i]) - cursor).Length;
                if (d < bestD) { bestD = d; best = HandleTargetBase + i; }
            }
            return best;
        }

        public bool Owns(int targetId) => Active && (targetId == ToolbarTargetId || targetId >= HandleTargetBase);

        public void Dispatch(PointerEvent e, Vector2 overlayPt)
        {
            if (e.TargetId == ToolbarTargetId)
            {
                if (e.Type == PointerEventType.Click) _toolbar.Click(overlayPt);
                return;
            }
            int idx = e.TargetId - HandleTargetBase;
            if (idx < 0 || idx >= SpriteJoints.All.Length) return;
            var name = SpriteJoints.All[idx];
            switch (e.Type)
            {
                case PointerEventType.DragStart:
                    _dragging = name;
                    MoveTo(name, e.Position);
                    break;
                case PointerEventType.DragMove:
                    if (_dragging == name) MoveTo(name, e.Position);
                    break;
                case PointerEventType.DragEnd:
                    if (_dragging == name) MoveTo(name, e.Position);
                    _dragging = null;
                    break;
            }
        }

        void MoveTo(string joint, Vec2 desktop)
        {
            var w = _space.DesktopToWorld(desktop);
            var local = ModelRoot.InverseTransformPoint(new Vector3(w.X, w.Y, ModelRoot.position.z));
            local.z = 0f;
            _model[joint] = local;
        }

        public void UpdateHover(Vector2 overlayPt) => _toolbar.UpdateHover(overlayPt);

        /// <summary>매 프레임: 점/선 위치 갱신. 캐릭터가 사라졌으면 종료.</summary>
        public void Update()
        {
            if (!Active) return;
            if (Actor == null || !Actor) { End(); return; }
            foreach (var kv in _handles)
                kv.Value.anchoredPosition = _space.DesktopToOverlayScreen(DesktopOf(kv.Key)).ToVector2();
            for (int i = 0; i < Links.Length; i++)
            {
                var a = _space.DesktopToOverlayScreen(DesktopOf(Links[i].a)).ToVector2();
                var b = _space.DesktopToOverlayScreen(DesktopOf(Links[i].b)).ToVector2();
                var line = _lines[i];
                var d = b - a;
                line.anchoredPosition = (a + b) * 0.5f;
                line.sizeDelta = new Vector2(Mathf.Max(1f, d.magnitude), 2f);
                line.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            }
        }

        /// <summary>오버레이 업로드 영역 (창 좌표, 위가 0).</summary>
        public void AddRegions(List<RectInt> regions, int overlayHeight)
        {
            if (!Active) return;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var kv in _handles)
            {
                var p = kv.Value.anchoredPosition;
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
            }
            const int pad = 24;
            regions.Add(new RectInt((int)x0 - pad, overlayHeight - (int)y1 - pad, (int)(x1 - x0) + pad * 2, (int)(y1 - y0) + pad * 2));
            if (_toolbar.IsOpen)
            {
                var m = _toolbar.ScreenRectOverlay;
                regions.Add(new RectInt((int)m.Left - 4, overlayHeight - (int)m.Bottom - 4, (int)m.Width + 8, (int)m.Height + 8));
            }
        }
    }
}
