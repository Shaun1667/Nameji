using System;
using System.Collections.Generic;
using System.Text;

namespace Shimeji.Core
{
    /// <summary>Unity HumanBodyBones와 같은 이름을 쓴다(엔진 쪽에서 Enum.Parse로 대응).</summary>
    public enum BoneKey
    {
        Hips, Spine, Chest, UpperChest, Neck, Head, LeftEye, RightEye,
        LeftShoulder, LeftUpperArm, LeftLowerArm, LeftHand,
        RightShoulder, RightUpperArm, RightLowerArm, RightHand,
        LeftUpperLeg, LeftLowerLeg, LeftFoot, LeftToes,
        RightUpperLeg, RightLowerLeg, RightFoot, RightToes,
    }

    public struct BoneNode
    {
        public string Name;
        public int Depth;
        public BoneNode(string name, int depth) { Name = name; Depth = depth; }
    }

    public sealed class BoneMapResult
    {
        public readonly Dictionary<BoneKey, string> Map = new Dictionary<BoneKey, string>();
        public readonly List<BoneKey> MissingRequired = new List<BoneKey>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>필수 본(Hips/Head/양발)이 모두 있으면 true. 아니면 로드 실패로 처리한다.</summary>
        public bool IsValid => MissingRequired.Count == 0;
        /// <summary>Unity 휴머노이드 아바타를 만들 수 있을 만큼 본이 있는지.</summary>
        public bool CanBuildHumanoid
        {
            get
            {
                foreach (var k in BoneMapper.HumanoidRequired) if (!Map.ContainsKey(k)) return false;
                return true;
            }
        }

        public bool TryGet(BoneKey k, out string name) => Map.TryGetValue(k, out name);
    }

    /// <summary>본 이름 휴리스틱 매핑 (Mixamo / Bip001 / VRM / Blender 등).</summary>
    public static class BoneMapper
    {
        public static readonly BoneKey[] Required = { BoneKey.Hips, BoneKey.Head, BoneKey.LeftFoot, BoneKey.RightFoot };

        public static readonly BoneKey[] HumanoidRequired =
        {
            BoneKey.Hips, BoneKey.Spine, BoneKey.Head,
            BoneKey.LeftUpperArm, BoneKey.LeftLowerArm, BoneKey.LeftHand,
            BoneKey.RightUpperArm, BoneKey.RightLowerArm, BoneKey.RightHand,
            BoneKey.LeftUpperLeg, BoneKey.LeftLowerLeg, BoneKey.LeftFoot,
            BoneKey.RightUpperLeg, BoneKey.RightLowerLeg, BoneKey.RightFoot,
        };

        static readonly HashSet<string> NoiseTokens = new HashSet<string>
        {
            "mixamorig", "j", "bip", "biped", "def", "org", "mch", "c", "center", "armature", "valvebiped", "bone", "jnt", "joint", "b", "skel",
        };

        static readonly HashSet<string> ExcludeTokens = new HashSet<string>
        {
            "twist", "end", "nub", "roll", "ik", "pole", "target", "sec", "adj", "hair", "skirt", "bust", "breast",
            "tail", "ear", "cloth", "ribbon", "thumb", "index", "middle", "ring", "little", "pinky", "finger", "fingers",
            "tip", "helper", "corrective", "jiggle", "ctrl", "control", "top", "footsteps", "prop", "weapon", "sleeve",
            "hood", "cape", "coat", "acc", "accessory", "dummy", "null", "share", "hat", "face", "mouth", "jaw", "tongue",
            "teeth", "brow", "eyebrow", "eyelid", "lid", "cheek", "nose", "lip",
        };

        enum Part
        {
            None, Hips, Spine, Chest, UpperChest, Neck, Head, Eye, Shoulder, UpperArm, LowerArm, Hand,
            UpperLeg, LowerLeg, AmbiguousLeg, Foot, Toes,
        }

        enum Side { None, Left, Right }

        struct Candidate
        {
            public string Name;
            public int Priority;
            public int Depth;
            public int Order;
        }

        public static BoneMapResult Map(IReadOnlyList<BoneNode> nodes, IDictionary<string, string> overrides = null)
        {
            var result = new BoneMapResult();
            var best = new Dictionary<BoneKey, Candidate>();
            var ambiguousLegs = new Dictionary<Side, Candidate>();
            var names = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (string.IsNullOrEmpty(n.Name)) continue;
                names.Add(n.Name);
                if (!Classify(n.Name, out var part, out var side, out var priority)) continue;
                var cand = new Candidate { Name = n.Name, Priority = priority, Depth = n.Depth, Order = i };

                if (part == Part.AmbiguousLeg)
                {
                    if (side == Side.None) continue;
                    if (!ambiguousLegs.TryGetValue(side, out var prev) || Better(cand, prev)) ambiguousLegs[side] = cand;
                    continue;
                }

                if (!ToKey(part, side, out var key)) continue;
                if (!best.TryGetValue(key, out var existing) || Better(cand, existing)) best[key] = cand;
            }

            // "Leg" 단독 이름: UpperLeg가 따로 있으면 LowerLeg(Mixamo LeftLeg), 없으면 UpperLeg.
            foreach (var kv in ambiguousLegs)
            {
                var upper = kv.Key == Side.Left ? BoneKey.LeftUpperLeg : BoneKey.RightUpperLeg;
                var lower = kv.Key == Side.Left ? BoneKey.LeftLowerLeg : BoneKey.RightLowerLeg;
                if (best.ContainsKey(upper)) { if (!best.ContainsKey(lower)) best[lower] = kv.Value; }
                else best[upper] = kv.Value;
            }

            foreach (var kv in best) result.Map[kv.Key] = kv.Value.Name;

            if (overrides != null)
            {
                foreach (var kv in overrides)
                {
                    if (!Enum.TryParse<BoneKey>(kv.Key, true, out var key))
                    {
                        result.Warnings.Add($"boneMapOverride: unknown bone key '{kv.Key}'");
                        continue;
                    }
                    if (!names.Contains(kv.Value))
                    {
                        result.Warnings.Add($"boneMapOverride: bone '{kv.Value}' not found for {key}");
                        continue;
                    }
                    result.Map[key] = kv.Value;
                }
            }

            foreach (var k in Required)
                if (!result.Map.ContainsKey(k)) result.MissingRequired.Add(k);
            return result;
        }

        static bool Better(Candidate a, Candidate b)
        {
            if (a.Priority != b.Priority) return a.Priority < b.Priority;
            if (a.Depth != b.Depth) return a.Depth < b.Depth;
            return a.Order < b.Order;
        }

        static bool ToKey(Part part, Side side, out BoneKey key)
        {
            key = default;
            bool l = side == Side.Left, r = side == Side.Right;
            switch (part)
            {
                case Part.Hips: key = BoneKey.Hips; return side == Side.None;
                case Part.Spine: key = BoneKey.Spine; return side == Side.None;
                case Part.Chest: key = BoneKey.Chest; return side == Side.None;
                case Part.UpperChest: key = BoneKey.UpperChest; return side == Side.None;
                case Part.Neck: key = BoneKey.Neck; return side == Side.None;
                case Part.Head: key = BoneKey.Head; return side == Side.None;
                case Part.Eye: key = l ? BoneKey.LeftEye : BoneKey.RightEye; return l || r;
                case Part.Shoulder: key = l ? BoneKey.LeftShoulder : BoneKey.RightShoulder; return l || r;
                case Part.UpperArm: key = l ? BoneKey.LeftUpperArm : BoneKey.RightUpperArm; return l || r;
                case Part.LowerArm: key = l ? BoneKey.LeftLowerArm : BoneKey.RightLowerArm; return l || r;
                case Part.Hand: key = l ? BoneKey.LeftHand : BoneKey.RightHand; return l || r;
                case Part.UpperLeg: key = l ? BoneKey.LeftUpperLeg : BoneKey.RightUpperLeg; return l || r;
                case Part.LowerLeg: key = l ? BoneKey.LeftLowerLeg : BoneKey.RightLowerLeg; return l || r;
                case Part.Foot: key = l ? BoneKey.LeftFoot : BoneKey.RightFoot; return l || r;
                case Part.Toes: key = l ? BoneKey.LeftToes : BoneKey.RightToes; return l || r;
            }
            return false;
        }

        /// <summary>이름을 토큰으로 쪼갠다: 구분자, camelCase, 문자/숫자 경계. 모두 소문자.</summary>
        public static List<string> Tokenize(string name)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            char prev = '\0';
            foreach (char c in name)
            {
                if (!char.IsLetterOrDigit(c))
                {
                    Flush(tokens, sb);
                    prev = '\0';
                    continue;
                }
                if (sb.Length > 0)
                {
                    bool boundary =
                        (char.IsLower(prev) && char.IsUpper(c)) ||
                        (char.IsLetter(prev) && char.IsDigit(c)) ||
                        (char.IsDigit(prev) && char.IsLetter(c));
                    if (boundary) Flush(tokens, sb);
                }
                sb.Append(char.ToLowerInvariant(c));
                prev = c;
            }
            Flush(tokens, sb);
            return tokens;
        }

        static void Flush(List<string> tokens, StringBuilder sb)
        {
            if (sb.Length == 0) return;
            tokens.Add(sb.ToString());
            sb.Clear();
        }

        static bool Classify(string name, out Part part, out Side side, out int priority)
        {
            part = Part.None;
            side = Side.None;
            priority = 0;
            var tokens = Tokenize(name);
            int index = -1;
            var core = new StringBuilder();
            foreach (var t in tokens)
            {
                if (ExcludeTokens.Contains(t)) return false;
                if (t == "l" || t == "left") { side = Side.Left; continue; }
                if (t == "r" || t == "right") { side = Side.Right; continue; }
                if (NoiseTokens.Contains(t)) continue;
                if (char.IsDigit(t[0]))
                {
                    if (core.Length > 0 && index < 0) index = int.Parse(t.Length > 6 ? t.Substring(0, 6) : t);
                    continue;
                }
                core.Append(t);
            }
            var s = core.ToString();
            if (s.Length == 0) return false;

            // 순서 중요: 더 구체적인 이름 먼저.
            if (s.Contains("upperchest")) part = Part.UpperChest;
            else if (s.Contains("chest")) part = Part.Chest;
            else if (s.Contains("spine"))
            {
                priority = 1; // 명시적 Chest/UpperChest가 있으면 그쪽이 우선
                part = index <= 0 ? Part.Spine : index == 1 ? Part.Chest : index == 2 ? Part.UpperChest : Part.None;
                if (index <= 0) priority = 0;
            }
            else if (s == "hips" || s == "hip" || s.Contains("pelvis") || s == "hipsroot") part = Part.Hips;
            else if (s.Contains("neck")) part = Part.Neck;
            else if (s.Contains("head")) part = Part.Head;
            else if (s.Contains("eye")) part = Part.Eye;
            else if (s.Contains("shoulder") || s.Contains("clavicle") || s.Contains("collar")) part = Part.Shoulder;
            else if (s.Contains("forearm") || s.Contains("lowerarm") || s.Contains("elbow")) part = Part.LowerArm;
            else if (s.Contains("upperarm") || s == "arm" || s == "uparm") part = Part.UpperArm;
            else if (s.Contains("hand") || s.Contains("wrist")) part = Part.Hand;
            else if (s.Contains("thigh") || s.Contains("upleg") || s.Contains("upperleg")) part = Part.UpperLeg;
            else if (s.Contains("calf") || s.Contains("shin") || s.Contains("lowerleg") || s.Contains("knee")) part = Part.LowerLeg;
            else if (s == "leg") part = Part.AmbiguousLeg;
            else if (s.Contains("toe")) part = Part.Toes;
            else if (s.Contains("foot") || s.Contains("ankle")) part = Part.Foot;

            // 손가락/눈 등 인덱스가 붙은 사지 본(예: Hand1)은 보조 본으로 본다.
            if (part != Part.None && part != Part.Spine && part != Part.Chest && part != Part.UpperChest && part != Part.Toes && index > 0)
                priority = 2;

            return part != Part.None;
        }
    }
}
