using System;
using System.Collections.Generic;
using System.IO;

namespace Shimeji.Core
{
    public enum ActivityLevel { Slow, Normal, Active }
    public enum HitTestMode { Collider, Alpha }

    /// <summary>앱 전역 설정 (persistentDataPath/settings.json).</summary>
    public sealed class AppSettings
    {
        public float GlobalScale = 1f;
        public ActivityLevel Activity = ActivityLevel.Normal;
        public bool AlwaysOnTop = true;
        public int TargetFrameRate = 30;
        public HitTestMode HitTest = HitTestMode.Collider;
        public bool LookAtCursor = true;
        public bool ShowDebugSurfaces;
        public bool RestorePositions = true;
        public bool HideOnFullscreen = true;
        /// <summary>사용자가 트레이에서 끈 캐릭터 폴더 이름.</summary>
        public List<string> DisabledCharacters = new List<string>();
        /// <summary>캐릭터별 마지막 위치 (데스크톱 픽셀, 발 위치).</summary>
        public Dictionary<string, Vec2> Positions = new Dictionary<string, Vec2>();

        public static float ActivityMultiplier(ActivityLevel a) =>
            a == ActivityLevel.Slow ? 0.5f : a == ActivityLevel.Active ? 1.8f : 1f;

        public static AppSettings Load(string path)
        {
            var s = new AppSettings();
            try
            {
                if (!File.Exists(path)) return s;
                var d = MiniJson.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
                if (d == null) return s;
                s.GlobalScale = MathUtil.Clamp(d.GetFloat("globalScale", s.GlobalScale), 0.25f, 4f);
                s.Activity = ParseEnum(d.GetString("activity"), s.Activity);
                s.AlwaysOnTop = d.GetBool("alwaysOnTop", s.AlwaysOnTop);
                int fps = d.GetInt("targetFrameRate", s.TargetFrameRate);
                s.TargetFrameRate = fps >= 45 ? 60 : 30;
                s.HitTest = ParseEnum(d.GetString("hitTest"), s.HitTest);
                s.LookAtCursor = d.GetBool("lookAtCursor", s.LookAtCursor);
                s.ShowDebugSurfaces = d.GetBool("showDebugSurfaces", s.ShowDebugSurfaces);
                s.RestorePositions = d.GetBool("restorePositions", s.RestorePositions);
                s.HideOnFullscreen = d.GetBool("hideOnFullscreen", s.HideOnFullscreen);
                var dis = d.GetStringArray("disabledCharacters");
                if (dis != null) s.DisabledCharacters = new List<string>(dis);
                var pos = d.GetObject("positions");
                if (pos != null)
                    foreach (var kv in pos)
                        if (kv.Value is List<object> a && a.Count == 2 && a[0] is double x && a[1] is double y)
                            s.Positions[kv.Key] = new Vec2((float)x, (float)y);
            }
            catch (Exception e)
            {
                Log.Warn("settings.json load failed, using defaults: " + e.Message);
            }
            return s;
        }

        public void Save(string path)
        {
            try
            {
                var pos = new Dictionary<string, object>();
                foreach (var kv in Positions) pos[kv.Key] = new List<object> { (double)kv.Value.X, (double)kv.Value.Y };
                var d = new Dictionary<string, object>
                {
                    ["globalScale"] = (double)GlobalScale,
                    ["activity"] = Activity.ToString(),
                    ["alwaysOnTop"] = AlwaysOnTop,
                    ["targetFrameRate"] = TargetFrameRate,
                    ["hitTest"] = HitTest.ToString(),
                    ["lookAtCursor"] = LookAtCursor,
                    ["showDebugSurfaces"] = ShowDebugSurfaces,
                    ["restorePositions"] = RestorePositions,
                    ["hideOnFullscreen"] = HideOnFullscreen,
                    ["disabledCharacters"] = new List<object>(DisabledCharacters),
                    ["positions"] = pos,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, MiniJson.Serialize(d));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Log.Warn("settings.json save failed: " + e.Message);
            }
        }

        static T ParseEnum<T>(string s, T def) where T : struct =>
            s != null && Enum.TryParse<T>(s, true, out var v) ? v : def;
    }
}
