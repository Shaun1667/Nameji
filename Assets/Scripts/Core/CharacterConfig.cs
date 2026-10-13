using System;
using System.Collections.Generic;

namespace Shimeji.Core
{
    public enum RigType { Auto, Humanoid, Generic }

    /// <summary>캐릭터 폴더의 character.json. 모든 필드에 기본값이 있다.</summary>
    public sealed class CharacterConfig
    {
        public string DisplayName;
        public float HeightPixels = 160f;
        /// <summary>오른쪽을 향할 때의 모델 Y 회전(도). 모델 정면이 +Z이면 90.</summary>
        public float FacingYawDeg = 90f;
        /// <summary>카메라 쪽으로 살짝 돌리는 3/4 뷰 각도(도).</summary>
        public float ViewAngleDeg = 20f;
        public RigType RigType = RigType.Auto;
        public Dictionary<string, string> BoneMapOverride = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ClipOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string BehaviorProfile = "default";
        public bool EnableSpringBones = true;
        public string ColliderPreset = "chibi";
        public float ColliderScale = 1f;
        /// <summary>SD 비율 보정: 팔을 바깥으로 벌리는 각도(도).</summary>
        public float ArmSpreadBias = 8f;
        public bool LookAtCursor = true;

        public const float MinHeight = 24f, MaxHeight = 1200f;

        public static CharacterConfig Default(string folderName)
        {
            return new CharacterConfig { DisplayName = folderName };
        }

        /// <summary>JSON을 파싱한다. 잘못된 값은 기본값으로 대체하고 warnings에 사유를 남긴다. 문법 오류도 예외 대신 기본값.</summary>
        public static CharacterConfig Parse(string json, string folderName, List<string> warnings = null)
        {
            var cfg = Default(folderName);
            if (string.IsNullOrWhiteSpace(json)) return cfg;

            Dictionary<string, object> d;
            try
            {
                d = MiniJson.Parse(json) as Dictionary<string, object>;
            }
            catch (Exception e)
            {
                warnings?.Add("character.json parse error: " + e.Message);
                return cfg;
            }
            if (d == null)
            {
                warnings?.Add("character.json root must be an object");
                return cfg;
            }

            var name = d.GetString("displayName");
            if (!string.IsNullOrWhiteSpace(name)) cfg.DisplayName = name;

            float h = d.GetFloat("heightPixels", cfg.HeightPixels);
            if (h < MinHeight || h > MaxHeight || float.IsNaN(h))
            {
                warnings?.Add($"heightPixels {h} out of range [{MinHeight},{MaxHeight}], using {cfg.HeightPixels}");
            }
            else cfg.HeightPixels = h;

            cfg.FacingYawDeg = d.GetFloat("facingYawDeg", cfg.FacingYawDeg);
            cfg.ViewAngleDeg = MathUtil.Clamp(d.GetFloat("viewAngleDeg", cfg.ViewAngleDeg), 0f, 60f);

            var rig = d.GetString("rigType");
            if (rig != null)
            {
                switch (rig.Trim().ToLowerInvariant())
                {
                    case "auto": cfg.RigType = RigType.Auto; break;
                    case "humanoid": cfg.RigType = RigType.Humanoid; break;
                    case "generic": cfg.RigType = RigType.Generic; break;
                    default: warnings?.Add($"unknown rigType '{rig}', using auto"); break;
                }
            }

            var bm = d.GetObject("boneMapOverride");
            if (bm != null)
                foreach (var kv in bm)
                    if (kv.Value is string s && !string.IsNullOrEmpty(s)) cfg.BoneMapOverride[kv.Key] = s;

            var co = d.GetObject("clipOverrides");
            if (co != null)
                foreach (var kv in co)
                    if (kv.Value is string s && !string.IsNullOrEmpty(s)) cfg.ClipOverrides[kv.Key] = s;

            var profile = d.GetString("behaviorProfile");
            if (!string.IsNullOrWhiteSpace(profile)) cfg.BehaviorProfile = profile.Trim();

            cfg.EnableSpringBones = d.GetBool("enableSpringBones", cfg.EnableSpringBones);
            var preset = d.GetString("colliderPreset");
            if (!string.IsNullOrWhiteSpace(preset)) cfg.ColliderPreset = preset.Trim();
            cfg.ColliderScale = MathUtil.Clamp(d.GetFloat("colliderScale", cfg.ColliderScale), 0.2f, 5f);
            cfg.ArmSpreadBias = MathUtil.Clamp(d.GetFloat("armSpreadBias", cfg.ArmSpreadBias), -45f, 45f);
            cfg.LookAtCursor = d.GetBool("lookAtCursor", cfg.LookAtCursor);
            return cfg;
        }
    }
}
