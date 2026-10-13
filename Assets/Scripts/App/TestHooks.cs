using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Shimeji.Core;
using UnityEngine;

namespace Shimeji.App
{
    /// <summary>
    /// 빌드 자동 테스트용 훅. 실행 인자 -shimeji-test 일 때만 활성화된다.
    ///  - Logs/status.json: 0.25초마다 오버레이/캐릭터 상태 기록
    ///  - Logs/commands.txt: 한 줄에 명령 하나 (처리 후 삭제)
    /// </summary>
    public sealed class TestHooks : IDisposable
    {
        readonly ShimejiApp _app;
        readonly string _statusPath, _commandPath;
        float _timer;
        string _lastResult = "";
        int _commandCount;

        TestHooks(ShimejiApp app)
        {
            _app = app;
            _statusPath = Path.Combine(app.LogsRoot, "status.json");
            _commandPath = Path.Combine(app.LogsRoot, "commands.txt");
            try { if (File.Exists(_commandPath)) File.Delete(_commandPath); } catch (Exception) { }
            Log.Info("TestHooks enabled: " + _statusPath);
        }

        public static TestHooks CreateIfRequested(ShimejiApp app)
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "-shimeji-test", StringComparison.OrdinalIgnoreCase)) return new TestHooks(app);
            return null;
        }

        public void Update(float dt)
        {
            ProcessCommands();
            _timer += dt;
            if (_timer < 0.25f) return;
            _timer = 0;
            WriteStatus();
        }

        static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        static float P(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        void ProcessCommands()
        {
            if (!File.Exists(_commandPath)) return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(_commandPath);
                File.Delete(_commandPath);
            }
            catch (IOException) { return; } // 쓰는 중이면 다음 프레임
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                try
                {
                    _lastResult = Execute(line.Split(' '));
                }
                catch (Exception e)
                {
                    _lastResult = "error: " + e.Message;
                }
                _commandCount++;
                Log.Info($"test command '{line}' → {_lastResult}");
            }
        }

        string Execute(string[] a)
        {
            var app = _app;
            switch (a[0].ToLowerInvariant())
            {
                case "place":
                {
                    var act = Need(a[1]);
                    act.Body.PlaceAt(new Vec2(P(a[2]), P(a[3])));
                    return "ok";
                }
                case "force":
                    return Need(a[1]).Brain.ForceBehavior(a[2]) ? "ok" : "rejected";
                case "drag":
                {
                    // drag key x1 y1 x2 y2 vx vy : 커서 x1,y1에서 잡아 x2,y2로 옮긴 뒤 속도 vx,vy로 놓는다
                    var act = Need(a[1]);
                    act.BeginDrag(new Vec2(P(a[2]), P(a[3])));
                    act.Drag(new Vec2(P(a[4]), P(a[5])));
                    act.EndDrag(new Vec2(P(a[6]), P(a[7])));
                    return "ok";
                }
                case "click": Need(a[1]).Brain.OnClick(); return "ok";
                case "dblclick": Need(a[1]).Brain.OnDoubleClick(); return "ok";
                case "scale": Need(a[1]).SetUserScale(P(a[2])); return "ok";
                case "remove": app.RemoveActor(Need(a[1])); return "ok";
                case "clone": app.Clone(Need(a[1])); return "ok";
                case "pause": app.SetPaused(a[1] == "on"); return "ok";
                case "debug": app.SetDebugSurfaces(a[1] == "on"); return "ok";
                case "tray": app.ExecuteTrayCommand(int.Parse(a[1])); return "ok";
                case "hittest":
                {
                    int id = app.HitTestCharacters(new Vec2(P(a[1]), P(a[2])));
                    var act = app.FindActor(id);
                    return act != null ? "hit " + act.InstanceKey : "none";
                }
                case "screenshot":
                    ScreenCapture.CaptureScreenshot(string.Join(" ", a, 1, a.Length - 1));
                    return "ok";
                case "quit":
                    Application.Quit();
                    return "ok";
            }
            return "unknown command";
        }

        Characters.CharacterActor Need(string key)
        {
            var a = _app.FindActor(key);
            if (a == null) throw new ArgumentException("no actor " + key);
            return a;
        }

        void WriteStatus()
        {
            var app = _app;
            var actors = new List<object>();
            foreach (var a in app.Actors)
            {
                actors.Add(new Dictionary<string, object>
                {
                    ["key"] = a.InstanceKey,
                    ["folder"] = a.FolderName,
                    ["runtimeId"] = a.RuntimeId,
                    ["x"] = a.Body.Position.X,
                    ["y"] = a.Body.Position.Y,
                    ["width"] = a.Body.Width,
                    ["height"] = a.Body.Height,
                    ["state"] = a.Body.State.ToString(),
                    ["surface"] = a.Body.Surface?.Type.ToString(),
                    ["owner"] = a.Body.Surface?.OwnerId ?? 0,
                    ["behavior"] = a.Brain.Current?.Id,
                    ["anim"] = a.Brain.AnimationKey,
                    ["animPlaying"] = a.Anim.CurrentKey,
                    ["source"] = a.Anim.CurrentSource.ToString(),
                    ["rig"] = a.Anim.RigKind.ToString(),
                    ["modelClips"] = new List<object>(a.Anim.ModelClipMap.Keys),
                    ["scale"] = a.UserScale,
                });
            }
            var ov = app.Space != null ? app.Space.Overlay : default;
            var d = new Dictionary<string, object>
            {
                ["time"] = Time.realtimeSinceStartup,
                ["frame"] = Time.frameCount,
                ["fps"] = Time.unscaledDeltaTime > 0 ? 1f / Time.unscaledDeltaTime : 0f,
                ["targetFps"] = Application.targetFrameRate,
                ["ready"] = app.Ready,
                ["overlay"] = new List<object> { ov.Left, ov.Top, ov.Right, ov.Bottom },
                ["screen"] = new List<object> { Screen.width, Screen.height },
                ["hwnd"] = app.Overlay != null ? app.Overlay.Hwnd.ToInt64() : 0,
                ["exStyle"] = app.Overlay != null ? (long)app.Overlay.ExStyle : 0,
                ["clickThrough"] = app.Overlay?.ClickThrough ?? false,
                ["hiddenFullscreen"] = app.HiddenForFullscreen,
                ["paused"] = app.PausedAll,
                ["surfaces"] = app.World.Surfaces.Count,
                ["windows"] = app.World.Windows.Count,
                ["monitors"] = app.World.Monitors.Count,
                ["commands"] = _commandCount,
                ["lastResult"] = _lastResult,
                ["memoryMB"] = GC.GetTotalMemory(false) / (1024.0 * 1024.0),
                ["actors"] = actors,
            };
            try
            {
                var tmp = _statusPath + ".tmp";
                File.WriteAllText(tmp, MiniJson.Serialize(d));
                if (File.Exists(_statusPath)) File.Delete(_statusPath);
                File.Move(tmp, _statusPath);
            }
            catch (IOException) { /* 읽는 쪽과 충돌 시 다음 기회 */ }
        }

        public void Dispose()
        {
        }
    }
}
