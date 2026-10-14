using System;
using System.Collections.Generic;
using Shimeji.Characters;
using Shimeji.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Shimeji.App.UI
{
    /// <summary>
    /// 2D 캐릭터 관절(앵커) 설정 화면.
    /// 이미지를 화면 가운데 크게 띄우고, 자동 감지한 관절을 미리 찍어 둔다.
    ///  - 목록에서 부위를 고르고 그림을 클릭하면 그 위치로 지정 → 다음 부위로 자동 이동
    ///  - 점을 드래그해서 옮기기
    ///  - 뼈대 선 실시간 표시
    /// 완료하면 관절(정규화 좌표)을 콜백으로 넘긴다. 클릭 통과 오버레이라 앱의 Win32 폴링 입력으로 조작한다.
    /// </summary>
    public sealed class RigSetupWizard
    {
        public const int TargetId = 1_000_002;
        const float SideWidth = 320f, Pad = 16f, ItemH = 24f, ButtonH = 34f, HandleSize = 14f, PickRadius = 14f;

        public sealed class Request
        {
            public string Name;
            public string Folder;
            public string ImagePath;
            public Texture2D Texture;
            public SpriteRigResult Detected;
            /// <summary>완료: 관절 → 이미지 정규화 좌표 (0..1, 좌상단 원점)</summary>
            public Action<Dictionary<string, Vec2>> OnDone;
            /// <summary>나중에: 자동 감지 결과로 그냥 진행</summary>
            public Action OnSkip;
        }

        readonly Font _font;
        readonly Camera _cam;
        readonly RectTransform _root;
        RectTransform _panel, _imageBox, _instruction;
        Text _instructionText, _titleText;
        RawImage _image;
        readonly Dictionary<string, RectTransform> _handles = new Dictionary<string, RectTransform>();
        readonly List<RectTransform> _lines = new List<RectTransform>();
        readonly List<RectTransform> _lineShadows = new List<RectTransform>();
        readonly List<(RectTransform rt, Image bg, string joint, Text label)> _items = new List<(RectTransform, Image, string, Text)>();
        readonly List<(RectTransform rt, Image bg, Action action)> _buttons = new List<(RectTransform, Image, Action)>();
        RectTransform _currentLabel;
        Text _currentLabelText;

        readonly Dictionary<string, Vec2> _joints = new Dictionary<string, Vec2>(); // 이미지 픽셀 좌표
        readonly HashSet<string> _userSet = new HashSet<string>();
        Request _req;
        string _current;
        string _dragging;

        public bool Active => _req != null;
        public string CurrentName => _req?.Name;
        public string CurrentJoint => _current;

        public RigSetupWizard(Transform canvas, Font font, Camera cam)
        {
            _font = font;
            _cam = cam;
            var go = new GameObject("RigSetupWizard", typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            _root = (RectTransform)go.transform;
            _root.anchorMin = _root.anchorMax = Vector2.zero;
            _root.pivot = Vector2.zero;
            go.SetActive(false);
        }

        // ------------------------------------------------------------------
        // 열기 / 닫기
        // ------------------------------------------------------------------

        /// <param name="area">표시할 영역 (오버레이 스크린 좌표, 아래가 0) — 보통 주 모니터 작업영역</param>
        public void Open(Request req, Rect area)
        {
            Close();
            _req = req;
            _joints.Clear();
            _userSet.Clear();
            foreach (var kv in req.Detected.Joints) _joints[kv.Key] = kv.Value;
            Build(area);
            Select(SpriteJoints.GuidedOrder[0]);
            _root.gameObject.SetActive(true);
            Refresh();
        }

        public void Close()
        {
            _req = null;
            _dragging = null;
            var children = new List<GameObject>();
            foreach (Transform c in _root) children.Add(c.gameObject);
            foreach (var c in children) UnityGlue.SafeDestroy(c);
            _handles.Clear();
            _lines.Clear();
            _lineShadows.Clear();
            _items.Clear();
            _buttons.Clear();
            _root.gameObject.SetActive(false);
        }

        /// <summary>완료 처리 (버튼과 같은 동작).</summary>
        public void Finish()
        {
            if (!Active) return;
            var req = _req;
            var result = new Dictionary<string, Vec2>();
            foreach (var kv in _joints)
                result[kv.Key] = new Vec2(kv.Value.X / req.Texture.width, kv.Value.Y / req.Texture.height);
            Close();
            req.OnDone?.Invoke(result);
        }

        public void Skip()
        {
            if (!Active) return;
            var req = _req;
            Close();
            req.OnSkip?.Invoke();
        }

        /// <summary>현재 관절을 자동 감지 위치로 전부 되돌린다.</summary>
        public void ResetToAuto()
        {
            if (!Active) return;
            foreach (var kv in _req.Detected.Joints) _joints[kv.Key] = kv.Value;
            _userSet.Clear();
            Select(SpriteJoints.GuidedOrder[0]);
            Refresh();
        }

        /// <summary>테스트/자동화용: 관절 하나를 이미지 정규화 좌표로 지정.</summary>
        public void SetJointNormalized(string joint, Vec2 n)
        {
            if (!Active || !_joints.ContainsKey(joint)) return;
            SetJoint(joint, new Vec2(n.X * _req.Texture.width, n.Y * _req.Texture.height));
            Refresh();
        }

        public Vec2 GetJointNormalized(string joint) =>
            Active && _joints.TryGetValue(joint, out var p) ? new Vec2(p.X / _req.Texture.width, p.Y / _req.Texture.height) : default;

        // ------------------------------------------------------------------
        // UI 구성
        // ------------------------------------------------------------------

        Rect _imageRect; // 오버레이 스크린 좌표

        void Build(Rect area)
        {
            var tex = _req.Texture;
            float maxH = Mathf.Min(area.height * 0.78f, 820f);
            float maxW = Mathf.Max(200f, area.width - SideWidth - Pad * 4);
            float h = maxH, w = h * tex.width / tex.height;
            if (w > maxW) { w = maxW; h = w * tex.height / tex.width; }
            float sideH = 70f + 70f + SpriteJoints.All.Length * ItemH + 4 * (ButtonH + 6f) + Pad;
            float panelW = w + SideWidth + Pad * 3, panelH = Mathf.Max(h, sideH) + Pad * 2;
            float px = area.x + (area.width - panelW) * 0.5f, py = area.y + (area.height - panelH) * 0.5f;

            _panel = Box("Panel", _root, new Rect(px, py, panelW, panelH), new Color(0.1f, 0.1f, 0.13f, 0.97f));
            // 이미지 영역 (체크무늬 대신 어두운 회색 배경)
            _imageRect = new Rect(px + Pad, py + panelH - Pad - h, w, h);
            _imageBox = Box("ImageBg", _root, _imageRect, new Color(0.25f, 0.27f, 0.3f, 1f));
            var imgGo = new GameObject("Image", typeof(RectTransform));
            imgGo.transform.SetParent(_imageBox, false);
            var irt = (RectTransform)imgGo.transform;
            irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = irt.offsetMax = Vector2.zero;
            _image = imgGo.AddComponent<RawImage>();
            _image.texture = tex;
            _image.raycastTarget = false;

            // 밝은 그림 위에서도 보이도록 어두운 외곽선 + 흰 선
            foreach (var _ in SpriteJoints.Links) _lineShadows.Add(Box("BoneShadow", _root, new Rect(0, 0, 1, 1), new Color(0.08f, 0.08f, 0.12f, 0.9f), centerPivot: true));
            foreach (var _ in SpriteJoints.Links) _lines.Add(Box("Bone", _root, new Rect(0, 0, 1, 1), new Color(1f, 1f, 1f, 0.95f), centerPivot: true));
            foreach (var j in SpriteJoints.All)
            {
                var c = (Color)SpriteSkeletonImage.JointColor(j);
                var hrt = Box("J_" + j, _root, new Rect(0, 0, HandleSize, HandleSize), c, centerPivot: true);
                _handles[j] = hrt;
            }
            _currentLabel = Box("CurrentLabel", _root, new Rect(0, 0, 150, 22), new Color(0f, 0f, 0f, 0.75f), centerPivot: false);
            _currentLabelText = OverlayUI.MakeText(_currentLabel, _font, "", 13, Color.white, TextAnchor.MiddleLeft);

            // 오른쪽 안내/목록/버튼
            float sx = px + Pad * 2 + w, top = py + panelH - Pad;
            var title = Box("Title", _root, new Rect(sx, top - 30, SideWidth, 30), Color.clear);
            _titleText = OverlayUI.MakeText(title, _font, "관절 설정 — " + _req.Name, 17, Color.white, TextAnchor.MiddleLeft);
            _instruction = Box("Instruction", _root, new Rect(sx, top - 30 - 74, SideWidth, 70), new Color(1f, 1f, 1f, 0.06f));
            _instructionText = OverlayUI.MakeText(_instruction, _font, "", 13, new Color(0.9f, 0.92f, 1f), TextAnchor.UpperLeft);
            _instructionText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _instructionText.verticalOverflow = VerticalWrapMode.Truncate;

            float y = top - 30 - 74 - 10;
            foreach (var j in SpriteJoints.GuidedOrder)
            {
                y -= ItemH;
                var rt = Box("Item_" + j, _root, new Rect(sx, y, SideWidth, ItemH - 2), Color.clear);
                var bg = rt.GetComponent<Image>();
                var label = OverlayUI.MakeText(rt, _font, "", 14, Color.white, TextAnchor.MiddleLeft);
                _items.Add((rt, bg, j, label));
            }
            y -= 10;
            AddButton("완료 — 이대로 저장하고 시작", ref y, sx, new Color(0.25f, 0.55f, 0.95f), Finish);
            AddButton("다음 부위", ref y, sx, new Color(0.3f, 0.32f, 0.38f), SelectNext);
            AddButton("자동 감지로 되돌리기", ref y, sx, new Color(0.3f, 0.32f, 0.38f), ResetToAuto);
            AddButton("나중에 (자동 감지로 시작)", ref y, sx, new Color(0.3f, 0.32f, 0.38f), Skip);
        }

        void AddButton(string text, ref float y, float x, Color color, Action action)
        {
            y -= ButtonH + 6f;
            var rt = Box("Button", _root, new Rect(x, y, SideWidth, ButtonH), color);
            OverlayUI.MakeText(rt, _font, text, 14, Color.white, TextAnchor.MiddleCenter);
            _buttons.Add((rt, rt.GetComponent<Image>(), action));
        }

        RectTransform Box(string name, Transform parent, Rect r, Color color, bool centerPivot = false)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = centerPivot ? new Vector2(0.5f, 0.5f) : Vector2.zero;
            rt.anchoredPosition = new Vector2(r.x, r.y);
            rt.sizeDelta = new Vector2(r.width, r.height);
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return rt;
        }

        // ------------------------------------------------------------------
        // 좌표 변환
        // ------------------------------------------------------------------

        Vector2 ImageToScreen(Vec2 p) => new Vector2(
            _imageRect.x + p.X / _req.Texture.width * _imageRect.width,
            _imageRect.y + (1f - p.Y / _req.Texture.height) * _imageRect.height);

        Vec2 ScreenToImage(Vector2 s) => new Vec2(
            Mathf.Clamp((s.x - _imageRect.x) / _imageRect.width, 0f, 1f) * _req.Texture.width,
            Mathf.Clamp(1f - (s.y - _imageRect.y) / _imageRect.height, 0f, 1f) * _req.Texture.height);

        // ------------------------------------------------------------------
        // 입력
        // ------------------------------------------------------------------

        /// <summary>오버레이 스크린 좌표가 설정 화면 위인지.</summary>
        public bool Contains(Vector2 overlayPt) =>
            Active && RectTransformUtility.RectangleContainsScreenPoint(_panel, overlayPt, _cam);

        string HandleAt(Vector2 s)
        {
            string best = null;
            float bestD = PickRadius;
            foreach (var kv in _joints)
            {
                float d = (ImageToScreen(kv.Value) - s).magnitude;
                if (d < bestD) { bestD = d; best = kv.Key; }
            }
            return best;
        }

        bool InImage(Vector2 s) => _imageRect.Contains(s);

        /// <param name="toOverlay">데스크톱 픽셀 → 오버레이 스크린 좌표</param>
        public void Dispatch(PointerEvent e, Func<Vec2, Vector2> toOverlay)
        {
            if (!Active) return;
            var s = toOverlay(e.Position);
            switch (e.Type)
            {
                case PointerEventType.DragStart:
                {
                    var h = HandleAt(s);
                    if (h != null) { _dragging = h; Select(h); }
                    else if (InImage(s)) { _dragging = _current; SetJoint(_current, ScreenToImage(s)); }
                    break;
                }
                case PointerEventType.DragMove:
                    if (_dragging != null) SetJoint(_dragging, ScreenToImage(s));
                    break;
                case PointerEventType.DragEnd:
                    if (_dragging != null) SetJoint(_dragging, ScreenToImage(s));
                    _dragging = null;
                    break;
                case PointerEventType.Click:
                case PointerEventType.DoubleClick:
                    Click(s);
                    break;
            }
            Refresh();
        }

        void Click(Vector2 s)
        {
            foreach (var b in _buttons)
                if (RectTransformUtility.RectangleContainsScreenPoint(b.rt, s, _cam)) { b.action(); return; }
            foreach (var it in _items)
                if (RectTransformUtility.RectangleContainsScreenPoint(it.rt, s, _cam)) { Select(it.joint); return; }
            if (!InImage(s)) return;
            var h = HandleAt(s);
            if (h != null && h != _current) { Select(h); return; }
            // 현재 부위를 클릭한 위치로 지정하고 다음 부위로
            SetJoint(_current, ScreenToImage(s));
            SelectNext();
        }

        void SetJoint(string joint, Vec2 p)
        {
            if (joint == null) return;
            _joints[joint] = p;
            _userSet.Add(joint);
            // 사용자가 따로 정하지 않은 팔꿈치/무릎은 중간점을 따라간다
            foreach (var j in new[] { SpriteJoints.ElbowA, SpriteJoints.ElbowB, SpriteJoints.KneeA, SpriteJoints.KneeB })
            {
                if (_userSet.Contains(j) || !SpriteJoints.TryGetMidpointParents(j, out var a, out var b)) continue;
                _joints[j] = (_joints[a] + _joints[b]) * 0.5f;
            }
        }

        void Select(string joint)
        {
            _current = joint;
        }

        void SelectNext()
        {
            int i = Array.IndexOf(SpriteJoints.GuidedOrder, _current);
            _current = SpriteJoints.GuidedOrder[(i + 1) % SpriteJoints.GuidedOrder.Length];
            Refresh();
        }

        public void UpdateHover(Vector2 s)
        {
            if (!Active) return;
            foreach (var b in _buttons)
            {
                bool over = RectTransformUtility.RectangleContainsScreenPoint(b.rt, s, _cam);
                var c = b.bg.color;
                b.bg.color = new Color(c.r, c.g, c.b, over ? 1f : 0.85f);
            }
        }

        // ------------------------------------------------------------------
        // 표시 갱신
        // ------------------------------------------------------------------

        public void Refresh()
        {
            if (!Active) return;
            foreach (var kv in _handles)
            {
                kv.Value.anchoredPosition = ImageToScreen(_joints[kv.Key]);
                float size = kv.Key == _current ? HandleSize * 1.7f : HandleSize;
                kv.Value.sizeDelta = new Vector2(size, size);
            }
            for (int i = 0; i < SpriteJoints.Links.Length; i++)
            {
                var a = ImageToScreen(_joints[SpriteJoints.Links[i].a]);
                var b = ImageToScreen(_joints[SpriteJoints.Links[i].b]);
                var d = b - a;
                var rot = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                var line = _lines[i];
                line.anchoredPosition = (a + b) * 0.5f;
                line.sizeDelta = new Vector2(Mathf.Max(1f, d.magnitude), 2.5f);
                line.localRotation = rot;
                var sh = _lineShadows[i];
                sh.anchoredPosition = line.anchoredPosition;
                sh.sizeDelta = new Vector2(Mathf.Max(1f, d.magnitude) + 2f, 6f);
                sh.localRotation = rot;
            }
            // 현재 관절 이름표
            if (_current != null)
            {
                var p = ImageToScreen(_joints[_current]);
                _currentLabel.anchoredPosition = p + new Vector2(14f, 6f);
                _currentLabelText.text = SpriteJoints.DisplayName(_current);
                _currentLabel.sizeDelta = new Vector2(_currentLabelText.preferredWidth + 24f, 22f);
                _currentLabel.SetAsLastSibling();
            }
            foreach (var it in _items)
            {
                bool cur = it.joint == _current;
                bool set = _userSet.Contains(it.joint);
                it.bg.color = cur ? new Color(0.3f, 0.45f, 0.85f, 0.9f) : Color.clear;
                it.label.text = $"{(set ? "●" : "○")}  {SpriteJoints.DisplayName(it.joint)}{(set ? "" : "  (자동)")}";
                it.label.color = set || cur ? Color.white : new Color(0.75f, 0.77f, 0.82f);
            }
            var conf = _req.Detected.Confidence;
            _instructionText.text =
                $"지금 지정할 부위: {SpriteJoints.DisplayName(_current)}\n" +
                "그림에서 위치를 클릭하면 지정되고 다음 부위로 넘어갑니다. 점은 드래그로도 옮길 수 있어요.\n" +
                $"자동 감지 신뢰도 {Mathf.RoundToInt(conf * 100)}%" + (conf < 0.7f ? " — 직접 확인을 권장합니다." : "");
        }

        /// <summary>오버레이 업로드 영역 (창 좌표, 위가 0).</summary>
        public void AddRegions(List<RectInt> regions, int overlayHeight)
        {
            if (!Active) return;
            var p = _panel.anchoredPosition;
            var s = _panel.sizeDelta;
            regions.Add(new RectInt((int)p.x - 30, overlayHeight - (int)(p.y + s.y) - 30, (int)s.x + 60, (int)s.y + 60));
        }
    }
}
