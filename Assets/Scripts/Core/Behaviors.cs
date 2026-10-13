using System;
using System.Collections.Generic;

namespace Shimeji.Core
{
    public enum MotionType
    {
        /// <summary>제자리 (대기, 앉기, 매달려 쉬기 등).</summary>
        Stand,
        /// <summary>지면 위를 좌우로 이동.</summary>
        Walk,
        /// <summary>벽에 붙어 오르내림 (속도 + = 위). 지면에서 시작하면 인접 벽에 붙는다.</summary>
        Climb,
        /// <summary>천장에 매달려 좌우 이동.</summary>
        Crawl,
        /// <summary>근처의 더 높은 표면으로 점프.</summary>
        Jump,
        /// <summary>붙어 있던 벽/천장에서 손을 놓는다.</summary>
        Drop,
    }

    public enum DirectionMode { Keep, Left, Right, Random, TowardWall, Opposite }

    /// <summary>
    /// 행동 정의 (데이터 주도). StreamingAssets/Behaviors/&lt;profile&gt;.json 에서 읽는다.
    /// 원조 시메지의 actions/behaviors XML과 같은 역할.
    /// </summary>
    public sealed class BehaviorDef
    {
        public string Id;
        public string AnimationKey = AnimKeys.Idle;
        public BodyState RequiredState = BodyState.Grounded;
        /// <summary>null/빈 배열이면 모든 표면 허용.</summary>
        public SurfaceType[] AllowedSurfaces;
        /// <summary>0이면 랜덤 선택 대상이 아님(시스템 행동: Fall, Held 등).</summary>
        public float Weight = 1f;
        public float DurationMin = 2f, DurationMax = 5f;
        public float SpeedMin, SpeedMax;
        public string[] NextBehaviors;
        /// <summary>조건 플래그 이름. '!' 접두사는 부정. 모두 만족해야 후보.</summary>
        public string[] Conditions;
        public bool Interruptible = true;
        /// <summary>한 번 쓰고 나서 다시 후보가 되기까지의 시간(초).</summary>
        public float Cooldown;
        public MotionType Motion = MotionType.Stand;
        public DirectionMode Direction = DirectionMode.Keep;
        /// <summary>사용자 입력이 없는 시간(분)당 가중치 증가량 (졸기 등).</summary>
        public float IdleWeightPerMinute;

        public override string ToString() => Id;
    }

    /// <summary>행동 선택에 쓰는 현재 상황.</summary>
    public sealed class BehaviorContext
    {
        public BodyState State;
        public SurfaceType? Surface;
        public readonly HashSet<string> Flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public float IdleSeconds;
        public double Time;

        public BehaviorContext Set(string flag, bool on = true)
        {
            if (on) Flags.Add(flag); else Flags.Remove(flag);
            return this;
        }
    }

    public static class ConditionFlags
    {
        public const string BlockedLeft = "BlockedLeft";
        public const string BlockedRight = "BlockedRight";
        public const string EdgeLeft = "EdgeLeft";
        public const string EdgeRight = "EdgeRight";
        public const string NearEdge = "NearEdge";
        public const string WallAdjacent = "WallAdjacent";
        public const string JumpTarget = "JumpTarget";
        public const string OnWindow = "OnWindow";
        public const string Sleepy = "Sleepy";
        public const string AtWallTop = "AtWallTop";
        public const string AtWallBottom = "AtWallBottom";
    }

    /// <summary>행동 프로필 JSON 파서.</summary>
    public static class BehaviorProfile
    {
        public static List<BehaviorDef> Parse(string json, List<string> errors = null)
        {
            var list = new List<BehaviorDef>();
            object root;
            try { root = MiniJson.Parse(json); }
            catch (Exception e) { errors?.Add("behavior profile parse error: " + e.Message); return list; }

            List<object> items = root as List<object>;
            if (items == null && root is Dictionary<string, object> d) items = d.GetArray("behaviors");
            if (items == null) { errors?.Add("behavior profile must be an array or {\"behaviors\": [...]}"); return list; }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in items)
            {
                if (!(o is Dictionary<string, object> b)) continue;
                var def = new BehaviorDef { Id = b.GetString("id") };
                if (string.IsNullOrWhiteSpace(def.Id)) { errors?.Add("behavior without id skipped"); continue; }
                if (!ids.Add(def.Id)) { errors?.Add($"duplicate behavior id '{def.Id}'"); continue; }
                def.AnimationKey = b.GetString("animationKey", def.AnimationKey);
                def.RequiredState = ParseEnum(b.GetString("requiredState"), def.RequiredState, errors, def.Id);
                var surfaces = b.GetStringArray("allowedSurfaces");
                if (surfaces != null && surfaces.Length > 0)
                {
                    var sl = new List<SurfaceType>();
                    foreach (var s in surfaces) sl.Add(ParseEnum(s, SurfaceType.Floor, errors, def.Id));
                    def.AllowedSurfaces = sl.ToArray();
                }
                def.Weight = Math.Max(0f, b.GetFloat("weight", def.Weight));
                var dur = b.GetArray("durationRange");
                if (dur != null && dur.Count == 2 && dur[0] is double d0 && dur[1] is double d1)
                {
                    def.DurationMin = (float)Math.Min(d0, d1);
                    def.DurationMax = (float)Math.Max(d0, d1);
                }
                var spd = b.GetArray("moveSpeedRange");
                if (spd != null && spd.Count == 2 && spd[0] is double s0 && spd[1] is double s1)
                {
                    def.SpeedMin = (float)Math.Min(s0, s1);
                    def.SpeedMax = (float)Math.Max(s0, s1);
                }
                def.NextBehaviors = b.GetStringArray("nextBehaviors");
                def.Conditions = b.GetStringArray("conditions");
                def.Interruptible = b.GetBool("interruptible", def.Interruptible);
                def.Cooldown = b.GetFloat("cooldown", def.Cooldown);
                def.Motion = ParseEnum(b.GetString("motion"), def.Motion, errors, def.Id);
                def.Direction = ParseEnum(b.GetString("direction"), def.Direction, errors, def.Id);
                def.IdleWeightPerMinute = b.GetFloat("idleWeightPerMinute", def.IdleWeightPerMinute);
                list.Add(def);
            }
            return list;
        }

        static T ParseEnum<T>(string s, T def, List<string> errors, string id) where T : struct
        {
            if (s == null) return def;
            if (Enum.TryParse<T>(s, true, out var v)) return v;
            errors?.Add($"behavior '{id}': unknown {typeof(T).Name} '{s}'");
            return def;
        }

        /// <summary>프로필 파일이 없거나 깨졌을 때 쓰는 최소 세트. 시스템 행동은 반드시 포함.</summary>
        public static List<BehaviorDef> Minimal()
        {
            return new List<BehaviorDef>
            {
                new BehaviorDef { Id = "Idle", AnimationKey = AnimKeys.Idle, Weight = 2, DurationMin = 2, DurationMax = 5 },
                new BehaviorDef { Id = "WalkLeft", AnimationKey = AnimKeys.Walk, Motion = MotionType.Walk, Direction = DirectionMode.Left, SpeedMin = 50, SpeedMax = 80, Weight = 2, Conditions = new[] { "!" + ConditionFlags.BlockedLeft } },
                new BehaviorDef { Id = "WalkRight", AnimationKey = AnimKeys.Walk, Motion = MotionType.Walk, Direction = DirectionMode.Right, SpeedMin = 50, SpeedMax = 80, Weight = 2, Conditions = new[] { "!" + ConditionFlags.BlockedRight } },
                new BehaviorDef { Id = "WallLetGo", AnimationKey = AnimKeys.Fall, RequiredState = BodyState.WallAttached, Motion = MotionType.Drop, Weight = 1 },
                new BehaviorDef { Id = "CeilingLetGo", AnimationKey = AnimKeys.Fall, RequiredState = BodyState.CeilingAttached, Motion = MotionType.Drop, Weight = 1 },
            };
        }
    }

    /// <summary>
    /// 가중치 + 조건 + 쿨다운 기반 행동 선택기. 시드 고정 System.Random으로 결정적 테스트 가능.
    /// </summary>
    public sealed class BehaviorSelector
    {
        readonly Dictionary<string, BehaviorDef> _byId = new Dictionary<string, BehaviorDef>(StringComparer.OrdinalIgnoreCase);
        readonly List<BehaviorDef> _all = new List<BehaviorDef>();
        readonly Dictionary<string, double> _lastUsed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        readonly Random _rng;

        /// <summary>활동량 배율: 이동 행동 가중치에 곱하고, 제자리 행동 가중치는 나눈다.</summary>
        public float Activity = 1f;

        public BehaviorSelector(IEnumerable<BehaviorDef> defs, Random rng)
        {
            _rng = rng ?? new Random();
            foreach (var d in defs)
            {
                if (_byId.ContainsKey(d.Id)) continue;
                _byId[d.Id] = d;
                _all.Add(d);
            }
        }

        public IReadOnlyList<BehaviorDef> All => _all;
        public Random Random => _rng;

        public BehaviorDef Get(string id) => id != null && _byId.TryGetValue(id, out var d) ? d : null;

        public void MarkUsed(BehaviorDef def, double time)
        {
            if (def != null) _lastUsed[def.Id] = time;
        }

        public bool IsEligible(BehaviorDef d, BehaviorContext ctx)
        {
            if (d.Weight <= 0f) return false;
            if (d.RequiredState != ctx.State) return false;
            if (d.AllowedSurfaces != null && d.AllowedSurfaces.Length > 0)
            {
                if (ctx.Surface == null || Array.IndexOf(d.AllowedSurfaces, ctx.Surface.Value) < 0) return false;
            }
            if (d.Conditions != null)
            {
                foreach (var c in d.Conditions)
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    bool neg = c[0] == '!';
                    var name = neg ? c.Substring(1) : c;
                    if (ctx.Flags.Contains(name) == neg) return false;
                }
            }
            if (d.Cooldown > 0f && _lastUsed.TryGetValue(d.Id, out var t) && ctx.Time - t < d.Cooldown) return false;
            return true;
        }

        public float EffectiveWeight(BehaviorDef d, BehaviorContext ctx)
        {
            float w = d.Weight + d.IdleWeightPerMinute * Math.Min(ctx.IdleSeconds / 60f, 30f);
            bool moving = d.Motion == MotionType.Walk || d.Motion == MotionType.Climb || d.Motion == MotionType.Crawl || d.Motion == MotionType.Jump;
            if (Activity > 0f) w = moving ? w * Activity : w / Activity;
            return w;
        }

        /// <summary>
        /// 다음 행동을 고른다. previous의 nextBehaviors 중 유효한 것이 있으면 그 안에서,
        /// 없으면 전체 풀에서. 직전과 같은 행동은 다른 후보가 있으면 제외한다.
        /// </summary>
        public BehaviorDef Select(BehaviorContext ctx, BehaviorDef previous)
        {
            var pool = new List<BehaviorDef>();
            if (previous?.NextBehaviors != null && previous.NextBehaviors.Length > 0)
            {
                foreach (var id in previous.NextBehaviors)
                {
                    var d = Get(id);
                    if (d != null && IsEligible(d, ctx)) pool.Add(d);
                }
            }
            if (pool.Count == 0)
                foreach (var d in _all) if (IsEligible(d, ctx)) pool.Add(d);
            if (pool.Count == 0) return null;

            if (previous != null && pool.Count > 1) pool.RemoveAll(d => d.Id == previous.Id);

            float total = 0f;
            foreach (var d in pool) total += EffectiveWeight(d, ctx);
            if (total <= 0f) return pool[0];
            double r = _rng.NextDouble() * total;
            foreach (var d in pool)
            {
                r -= EffectiveWeight(d, ctx);
                if (r <= 0) return d;
            }
            return pool[pool.Count - 1];
        }

        public float NextFloat(float min, float max) => min + (float)_rng.NextDouble() * (max - min);
    }
}
