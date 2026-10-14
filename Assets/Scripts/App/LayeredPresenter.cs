using System;
using System.Collections.Generic;
using Shimeji.Core;
using Shimeji.Platform.Win32;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Shimeji.App
{
    /// <summary>
    /// 카메라를 RenderTexture에 렌더하고, 바뀐 영역(캐릭터/말풍선/메뉴 주변)만 비동기로 읽어
    /// LayeredOverlayWindow의 DIB에 프리멀티플 BGRA로 써서 화면에 올린다.
    /// 전체 화면을 매 프레임 읽지 않으므로 CPU 비용이 작다.
    /// </summary>
    public sealed class LayeredPresenter : IDisposable
    {
        readonly Camera _camera;
        readonly LayeredOverlayWindow _window;
        RenderTexture _rt;
        readonly List<RectInt> _current = new List<RectInt>();
        readonly List<RectInt> _previous = new List<RectInt>();
        readonly List<RectInt> _requests = new List<RectInt>();
        readonly List<RectInt> _retry = new List<RectInt>();
        int _pending;
        bool _fullRefresh = true;
        public int MaxPending = 24;
        public int UploadedPixelsLastSecond { get; private set; }
        int _pixelCounter;
        float _counterTime;

        public RenderTexture Target => _rt;

        public LayeredPresenter(Camera camera, LayeredOverlayWindow window)
        {
            _camera = camera;
            _window = window;
            Resize();
            Camera.onPostRender += OnPostRender;
        }

        public void Resize()
        {
            if (_rt != null)
            {
                _camera.targetTexture = null;
                _rt.Release();
                UnityEngine.Object.Destroy(_rt);
            }
            var desc = new RenderTextureDescriptor(_window.Width, _window.Height, RenderTextureFormat.ARGB32, 16)
            {
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear,
                msaaSamples = 1,
            };
            _rt = new RenderTexture(desc) { name = "OverlayRT" };
            _rt.Create();
            _camera.targetTexture = _rt;
            _fullRefresh = true;
        }

        /// <summary>이번 프레임에 그려진 내용이 있는 영역 (창 좌표, 위가 0). 다음 렌더 후 읽어 올린다.</summary>
        public void SetRegions(IEnumerable<RectInt> regions, bool fullFrame)
        {
            _current.Clear();
            if (fullFrame) _current.Add(new RectInt(0, 0, _window.Width, _window.Height));
            else foreach (var r in regions) AddClamped(_current, r);
        }

        void AddClamped(List<RectInt> list, RectInt r)
        {
            int x0 = Mathf.Clamp(r.xMin, 0, _window.Width), x1 = Mathf.Clamp(r.xMax, 0, _window.Width);
            int y0 = Mathf.Clamp(r.yMin, 0, _window.Height), y1 = Mathf.Clamp(r.yMax, 0, _window.Height);
            if (x1 > x0 && y1 > y0) list.Add(new RectInt(x0, y0, x1 - x0, y1 - y0));
        }

        void OnPostRender(Camera cam)
        {
            if (cam != _camera || _rt == null || !_window.IsValid) return;

            if (_pending > MaxPending)
            {
                // GPU가 밀리면 이번 프레임은 건너뛴다. 이번에 그린 영역은 "다음에 반드시 올릴 영역"에 누적해
                // 캐릭터가 떠난 자리가 지워지지 않고 남는 잔상이 생기지 않게 한다.
                _previous.AddRange(_current);
                Merge(_previous);
                return;
            }

            _requests.Clear();
            if (_fullRefresh)
            {
                _requests.Add(new RectInt(0, 0, _window.Width, _window.Height));
                _fullRefresh = false;
            }
            else
            {
                // 지금 영역 + 지난번 올린 이후 내용이 있었던 영역(떠난 자리를 투명으로 지우기 위해) + 실패한 영역
                _requests.AddRange(_current);
                _requests.AddRange(_previous);
                _requests.AddRange(_retry);
                Merge(_requests);
            }
            _retry.Clear();
            _previous.Clear();
            _previous.AddRange(_current);

            foreach (var r in _requests)
            {
                int texY = _window.Height - r.yMax; // RT는 아래가 0
                var region = r;
                _pending++;
                AsyncGPUReadback.Request(_rt, 0, r.x, r.width, texY, r.height, 0, 1, TextureFormat.RGBA32, req => OnReadback(req, region));
            }
        }

        /// <summary>겹치거나 맞닿은 사각형을 합친다 (읽기 요청 수를 줄임).</summary>
        static void Merge(List<RectInt> rects)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < rects.Count && !merged; i++)
                    for (int j = i + 1; j < rects.Count && !merged; j++)
                    {
                        var a = rects[i];
                        var b = rects[j];
                        if (a.xMin <= b.xMax && b.xMin <= a.xMax && a.yMin <= b.yMax && b.yMin <= a.yMax)
                        {
                            int x0 = Math.Min(a.xMin, b.xMin), y0 = Math.Min(a.yMin, b.yMin);
                            int x1 = Math.Max(a.xMax, b.xMax), y1 = Math.Max(a.yMax, b.yMax);
                            rects[i] = new RectInt(x0, y0, x1 - x0, y1 - y0);
                            rects.RemoveAt(j);
                            merged = true;
                        }
                    }
            }
        }

        unsafe void OnReadback(AsyncGPUReadbackRequest req, RectInt r)
        {
            _pending--;
            if (!_window.IsValid) return;
            if (req.hasError)
            {
                _retry.Add(r); // 다음 프레임에 다시 읽는다
                return;
            }
            if (r.xMax > _window.Width || r.yMax > _window.Height) return; // 크기 변경 직후 오래된 요청
            var data = req.GetData<byte>();
            byte* src = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(data);
            byte* dib = (byte*)_window.Bits;
            int stride = _window.Width * 4;
            int w = r.width, h = r.height;
            for (int row = 0; row < h; row++)
            {
                // 읽은 데이터는 아래 행부터. 창(DIB)은 위 행부터.
                byte* s = src + row * w * 4;
                byte* d = dib + (r.y + h - 1 - row) * stride + r.x * 4;
                for (int i = 0; i < w; i++, s += 4, d += 4)
                {
                    byte a = s[3];
                    // 투명 배경(0,0,0,0) 위에 그려진 색은 이미 알파가 곱해진 것과 같다. 단 색이 알파를 넘지 않게 제한.
                    byte rr = s[0], gg = s[1], bb = s[2];
                    if (rr > a) rr = a;
                    if (gg > a) gg = a;
                    if (bb > a) bb = a;
                    d[0] = bb; d[1] = gg; d[2] = rr; d[3] = a;
                }
            }
            _window.Present(r.x, r.y, w, h);

            _pixelCounter += w * h;
            if (Time.unscaledTime - _counterTime > 1f)
            {
                UploadedPixelsLastSecond = _pixelCounter;
                _pixelCounter = 0;
                _counterTime = Time.unscaledTime;
            }
        }

        /// <summary>
        /// 진단: 렌더 결과(rtCopy, 아래가 0)와 창 DIB를 비교해 "창에는 남아 있는데 렌더에는 없는" 잔상 픽셀 수를 센다.
        /// </summary>
        public unsafe int CountGhostPixels(Texture2D rtCopy, out RectInt bounds)
        {
            bounds = default;
            if (!_window.IsValid || rtCopy.width != _window.Width || rtCopy.height != _window.Height) return -1;
            var px = rtCopy.GetPixels32();
            byte* dib = (byte*)_window.Bits;
            int w = _window.Width, h = _window.Height, ghosts = 0;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++)
            {
                byte* row = dib + y * w * 4;
                int srcRow = (h - 1 - y) * w;
                for (int x = 0; x < w; x++)
                {
                    if (row[x * 4 + 3] <= 8 || px[srcRow + x].a != 0) continue;
                    ghosts++;
                    if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y;
                }
            }
            if (ghosts > 0) bounds = new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            return ghosts;
        }

        /// <summary>DIB에서 커서 위치 픽셀의 알파 (정밀 히트테스트용).</summary>
        public unsafe float AlphaAt(int x, int y)
        {
            if (!_window.IsValid || x < 0 || y < 0 || x >= _window.Width || y >= _window.Height) return 0f;
            byte* dib = (byte*)_window.Bits;
            return dib[y * _window.Width * 4 + x * 4 + 3] / 255f;
        }

        public void Dispose()
        {
            Camera.onPostRender -= OnPostRender;
            if (_camera != null) _camera.targetTexture = null;
            if (_rt != null)
            {
                _rt.Release();
                UnityEngine.Object.Destroy(_rt);
            }
        }
    }
}
