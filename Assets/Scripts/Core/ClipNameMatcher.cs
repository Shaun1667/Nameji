using System;
using System.Collections.Generic;
using System.Text;

namespace Shimeji.Core
{
    /// <summary>애니메이션 상태 키 (6.2 공용 클립 라이브러리).</summary>
    public static class AnimKeys
    {
        public const string Idle = "Idle";
        public const string IdleLookAround = "IdleLookAround";
        public const string Walk = "Walk";
        public const string Run = "Run";
        public const string Sit = "Sit";
        public const string SitEdge = "SitEdge";
        public const string Sleep = "Sleep";
        public const string Jump = "Jump";
        public const string Fall = "Fall";
        public const string Land = "Land";
        public const string Dragged = "Dragged";
        public const string Thrown = "Thrown";
        public const string ClimbWall = "ClimbWall";
        public const string HangCeiling = "HangCeiling";
        public const string CrawlCeiling = "CrawlCeiling";
        public const string ClickReact = "ClickReact";
        public const string Wave = "Wave";
        public const string Dance = "Dance";

        public static readonly string[] All =
        {
            Idle, IdleLookAround, Walk, Run, Sit, SitEdge, Sleep, Jump, Fall, Land, Dragged, Thrown,
            ClimbWall, HangCeiling, CrawlCeiling, ClickReact, Wave, Dance,
        };

        /// <summary>클립이 없을 때 대신 쓸 키.</summary>
        public static string Fallback(string key)
        {
            switch (key)
            {
                case IdleLookAround: case Wave: case ClickReact: case Land: return Idle;
                case Run: return Walk;
                case SitEdge: case Sleep: return Sit;
                case Jump: case Thrown: case Dragged: return Fall;
                case CrawlCeiling: return HangCeiling;
                case Dance: return Wave;
                default: return Idle;
            }
        }
    }

    /// <summary>모델 내장 클립 이름을 상태 키에 대응시킨다 (대소문자 무시, 접두사 허용).</summary>
    public static class ClipNameMatcher
    {
        /// <summary>"Armature|Walk_Loop" → "walkloop" 같은 정규화. 마지막 '|' 또는 ':' 뒤만 사용.</summary>
        public static string Normalize(string clipName)
        {
            if (string.IsNullOrEmpty(clipName)) return "";
            int cut = Math.Max(clipName.LastIndexOf('|'), clipName.LastIndexOf(':'));
            var s = cut >= 0 ? clipName.Substring(cut + 1) : clipName;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            var r = sb.ToString();
            // 흔한 접미사 제거
            foreach (var suffix in new[] { "loop", "anim", "animation" })
                if (r.Length > suffix.Length && r.EndsWith(suffix)) r = r.Substring(0, r.Length - suffix.Length);
            return r;
        }

        public static bool Matches(string clipName, string key)
        {
            var n = Normalize(clipName);
            var k = key.ToLowerInvariant();
            if (n.Length == 0) return false;
            if (n == k) return true;
            // 접두사 허용: "chibi_walk" → walk. 단 더 긴 키에 먹히지 않도록 다른 키와 정확히 일치하면 제외.
            return n.EndsWith(k);
        }

        /// <summary>
        /// 각 키에 대해 가장 알맞은 클립 이름을 고른다. 정확 일치 &gt; 접두사 일치(짧은 이름 우선).
        /// 하나의 클립이 여러 키에 걸리면(예: "idlelookaround"는 Idle이 아님) 더 긴 키가 이긴다.
        /// </summary>
        public static Dictionary<string, string> Match(IEnumerable<string> clipNames, IEnumerable<string> keys)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var keyList = new List<string>(keys);
            // 긴 키부터 처리해서 "IdleLookAround"가 "Idle"보다 먼저 클립을 가져가게 한다.
            keyList.Sort((a, b) => b.Length.CompareTo(a.Length));
            var clips = new List<string>(clipNames);
            var taken = new HashSet<string>();
            foreach (var key in keyList)
            {
                string best = null;
                int bestScore = int.MaxValue;
                foreach (var c in clips)
                {
                    if (taken.Contains(c) || !Matches(c, key)) continue;
                    int score = Normalize(c) == key.ToLowerInvariant() ? 0 : 1000 + Normalize(c).Length;
                    if (score < bestScore) { bestScore = score; best = c; }
                }
                if (best != null) { result[key] = best; taken.Add(best); }
            }
            return result;
        }
    }
}
