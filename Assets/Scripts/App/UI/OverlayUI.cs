using System;
using System.Collections.Generic;
using Shimeji.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Shimeji.App.UI
{
    /// <summary>
    /// 오버레이 안에 그리는 uGUI (컨텍스트 메뉴, 말풍선).
    /// 클릭 통과 창에서는 Unity 입력이 오지 않으므로 EventSystem을 쓰지 않고
    /// Win32 폴링 좌표로 직접 히트테스트한다.
    /// </summary>
    public sealed class OverlayUI
    {
        public readonly Canvas Canvas;
        readonly Font _font;
        public ContextMenuView Menu { get; }

        public OverlayUI(Camera camera)
        {
            var go = new GameObject("OverlayUI");
            UnityEngine.Object.DontDestroyOnLoad(go);
            Canvas = go.AddComponent<Canvas>();
            // 카메라 렌더 텍스처(ULW 백엔드)에 같이 그려지도록 Screen Space - Camera
            Canvas.renderMode = RenderMode.ScreenSpaceCamera;
            Canvas.worldCamera = camera;
            Canvas.planeDistance = 10f;
            Canvas.sortingOrder = 100;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            _font = LoadFont();
            Menu = new ContextMenuView(Canvas.transform, _font, camera);
        }

        static Font LoadFont()
        {
            try
            {
                var f = Font.CreateDynamicFontFromOSFont(new[] { "Malgun Gothic", "맑은 고딕", "Segoe UI", "Arial" }, 15);
                if (f != null) return f;
            }
            catch (Exception) { /* OS 폰트 실패 시 기본 폰트 */ }
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        public SpeechBubble CreateBubble() => new SpeechBubble(Canvas.transform, _font);

        public JointEditor CreateJointEditor(Camera cam) => new JointEditor(Canvas.transform, _font, cam);

        public static Text MakeText(Transform parent, Font font, string text, int size, Color color, TextAnchor anchor)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(10, 0);
            rt.offsetMax = new Vector2(-10, 0);
            return t;
        }
    }

    public sealed class ContextMenuView
    {
        public struct Item
        {
            public string Label;
            public Action Action;
            public Item(string label, Action action) { Label = label; Action = action; }
        }

        const float ItemHeight = 30f, Width = 190f, Padding = 6f;
        static readonly Color Bg = new Color(0.12f, 0.12f, 0.15f, 0.96f);
        static readonly Color Hover = new Color(0.30f, 0.45f, 0.85f, 1f);

        readonly RectTransform _panel;
        readonly Font _font;
        readonly Camera _cam;
        readonly List<(RectTransform rt, Image bg, Action action)> _items = new List<(RectTransform, Image, Action)>();
        public int TargetRuntimeId { get; private set; } = -1;
        public bool IsOpen => _panel.gameObject.activeSelf;

        public ContextMenuView(Transform canvas, Font font, Camera cam)
        {
            _font = font;
            _cam = cam;
            var go = new GameObject("ContextMenu", typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            _panel = (RectTransform)go.transform;
            _panel.pivot = new Vector2(0, 1);
            _panel.anchorMin = _panel.anchorMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = Bg;
            img.raycastTarget = false;
            go.SetActive(false);
        }

        /// <summary>overlayScreenPos: Unity Screen 좌표(좌하단 원점).</summary>
        public void Open(int targetId, Vector2 overlayScreenPos, IList<Item> items)
        {
            foreach (var it in _items) UnityEngine.Object.Destroy(it.rt.gameObject);
            _items.Clear();
            TargetRuntimeId = targetId;
            float h = items.Count * ItemHeight + Padding * 2;
            _panel.sizeDelta = new Vector2(Width, h);
            for (int i = 0; i < items.Count; i++)
            {
                var go = new GameObject("Item", typeof(RectTransform));
                go.transform.SetParent(_panel, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = new Vector2(0, 1);
                rt.anchorMax = new Vector2(1, 1);
                rt.pivot = new Vector2(0.5f, 1);
                rt.sizeDelta = new Vector2(-Padding * 2, ItemHeight);
                rt.anchoredPosition = new Vector2(0, -Padding - i * ItemHeight);
                var bg = go.AddComponent<Image>();
                bg.color = Color.clear;
                bg.raycastTarget = false;
                OverlayUI.MakeText(go.transform, _font, items[i].Label, 15, Color.white, TextAnchor.MiddleLeft);
                _items.Add((rt, bg, items[i].Action));
            }
            // 화면 밖으로 나가지 않게
            float x = Mathf.Clamp(overlayScreenPos.x, 0, _cam.pixelWidth - Width);
            float y = Mathf.Clamp(overlayScreenPos.y, h, _cam.pixelHeight);
            _panel.anchoredPosition = new Vector2(x, y);
            _panel.gameObject.SetActive(true);
        }

        public void Close()
        {
            _panel.gameObject.SetActive(false);
            TargetRuntimeId = -1;
        }

        public bool Contains(Vector2 overlayScreenPos) =>
            IsOpen && RectTransformUtility.RectangleContainsScreenPoint(_panel, overlayScreenPos, _cam);

        public void UpdateHover(Vector2 overlayScreenPos)
        {
            if (!IsOpen) return;
            foreach (var it in _items)
                it.bg.color = RectTransformUtility.RectangleContainsScreenPoint(it.rt, overlayScreenPos, _cam) ? Hover : Color.clear;
        }

        /// <summary>메뉴 클릭 처리. 항목을 눌렀으면 실행 후 닫는다.</summary>
        public void Click(Vector2 overlayScreenPos)
        {
            if (!IsOpen) return;
            foreach (var it in _items)
            {
                if (!RectTransformUtility.RectangleContainsScreenPoint(it.rt, overlayScreenPos, _cam)) continue;
                var action = it.action;
                Close();
                try { action?.Invoke(); }
                catch (Exception e) { Log.Error("menu action failed: " + e); }
                return;
            }
        }

        public RectPx ScreenRectOverlay
        {
            get
            {
                var p = _panel.anchoredPosition;
                var s = _panel.sizeDelta;
                return new RectPx(p.x, p.y - s.y, p.x + s.x, p.y);
            }
        }
    }

    /// <summary>머리 위 말풍선.</summary>
    public sealed class SpeechBubble
    {
        readonly RectTransform _rt;
        readonly Text _text;
        float _hideAt;

        public SpeechBubble(Transform canvas, Font font)
        {
            var go = new GameObject("Bubble", typeof(RectTransform));
            go.transform.SetParent(canvas, false);
            _rt = (RectTransform)go.transform;
            _rt.anchorMin = _rt.anchorMax = Vector2.zero;
            _rt.pivot = new Vector2(0.5f, 0f);
            _rt.sizeDelta = new Vector2(90, 34);
            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.95f);
            img.raycastTarget = false;
            _text = OverlayUI.MakeText(go.transform, font, "", 16, new Color(0.15f, 0.15f, 0.2f), TextAnchor.MiddleCenter);
            go.SetActive(false);
        }

        public void Show(string text, float seconds, float now)
        {
            _text.text = text;
            _rt.sizeDelta = new Vector2(Mathf.Max(44, _text.preferredWidth + 24), 34);
            _hideAt = now + seconds;
            _rt.gameObject.SetActive(true);
        }

        public void Update(Vector2 anchorOverlayScreen, float now)
        {
            if (!_rt.gameObject.activeSelf) return;
            if (now >= _hideAt) { _rt.gameObject.SetActive(false); return; }
            _rt.anchoredPosition = anchorOverlayScreen + new Vector2(0, 8);
        }

        public bool Visible => _rt.gameObject.activeSelf;

        /// <summary>오버레이 스크린 좌표(아래가 0) 사각형.</summary>
        public Rect ScreenRect
        {
            get
            {
                var p = _rt.anchoredPosition;
                var s = _rt.sizeDelta;
                return new Rect(p.x - s.x * 0.5f, p.y, s.x, s.y);
            }
        }

        public void Destroy()
        {
            if (_rt != null) UnityEngine.Object.Destroy(_rt.gameObject);
        }
    }
}
