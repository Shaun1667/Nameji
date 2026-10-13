using System;

namespace Shimeji.Core
{
    /// <summary>시스템(인터럽트) 행동 ID. 프로필에 없으면 기본 정의를 쓴다.</summary>
    public static class SystemBehaviors
    {
        public const string Fall = "Fall";
        public const string Land = "Land";
        public const string Held = "Held";
        public const string Thrown = "Thrown";
        public const string ClickReact = "ClickReact";
        public const string DoubleClickReact = "DoubleClickReact";
        public const string Idle = "Idle";

        public static BehaviorDef CreateDefault(string id)
        {
            switch (id)
            {
                case Fall: return new BehaviorDef { Id = Fall, AnimationKey = AnimKeys.Fall, RequiredState = BodyState.Airborne, Weight = 0, DurationMin = 999, DurationMax = 999, Interruptible = true };
                case Thrown: return new BehaviorDef { Id = Thrown, AnimationKey = AnimKeys.Thrown, RequiredState = BodyState.Airborne, Weight = 0, DurationMin = 999, DurationMax = 999 };
                case Land: return new BehaviorDef { Id = Land, AnimationKey = AnimKeys.Land, Weight = 0, DurationMin = 0.35f, DurationMax = 0.45f, Interruptible = false };
                case Held: return new BehaviorDef { Id = Held, AnimationKey = AnimKeys.Dragged, RequiredState = BodyState.Held, Weight = 0, DurationMin = 999, DurationMax = 999 };
                case ClickReact: return new BehaviorDef { Id = ClickReact, AnimationKey = AnimKeys.ClickReact, Weight = 0, DurationMin = 1.2f, DurationMax = 1.5f, Interruptible = false };
                case DoubleClickReact: return new BehaviorDef { Id = DoubleClickReact, AnimationKey = AnimKeys.Dance, Weight = 0, DurationMin = 3f, DurationMax = 4f };
                case Idle: return new BehaviorDef { Id = Idle, AnimationKey = AnimKeys.Idle, Weight = 0, DurationMin = 1f, DurationMax = 2f };
            }
            return null;
        }
    }

    /// <summary>
    /// 행동 상태머신 (순수 C#). 매 프레임 Tick에서 몸체 물리를 진행하고,
    /// 이벤트/조건에 따라 행동을 고르며, 재생할 애니메이션 키를 결정한다.
    /// </summary>
    public sealed class CharacterBrain
    {
        public readonly CharacterBody Body;
        public readonly BehaviorSelector Selector;

        public BehaviorDef Current { get; private set; }
        public float Remaining { get; private set; }
        /// <summary>바라보는 방향: +1 오른쪽, -1 왼쪽.</summary>
        public int Facing { get; private set; } = 1;
        public float Speed { get; private set; }
        public double Time { get; private set; }
        public float IdleSeconds { get; private set; }
        public bool Paused { get; set; }
        public float MaxJumpHeight = 420f;
        public float MaxJumpDistance = 520f;
        /// <summary>낭떠러지에 다다랐을 때 멈출 확률.</summary>
        public float StopAtEdgeChance = 0.7f;
        /// <summary>던져진 것으로 볼 최소 속도(px/s).</summary>
        public float ThrowSpeedThreshold = 600f;

        public string AnimationKey { get; private set; } = AnimKeys.Idle;
        /// <summary>이동 속도 기반 애니메이션 재생 배율 (발 미끄러짐 방지용).</summary>
        public float AnimationSpeed { get; private set; } = 1f;
        public BodyEvents LastEvents { get; private set; }
        /// <summary>행동이 바뀔 때마다 증가 (렌더 쪽 크로스페이드 트리거).</summary>
        public int BehaviorSerial { get; private set; }

        bool _edgeDecisionMade;
        bool _walkOffEdge;
        Vec2? _jumpTarget;

        public CharacterBrain(CharacterBody body, BehaviorSelector selector)
        {
            Body = body;
            Selector = selector;
        }

        BehaviorDef Sys(string id) => Selector.Get(id) ?? SystemBehaviors.CreateDefault(id);

        // ---------------- 입력 ----------------

        public void NotifyUserActivity() => IdleSeconds = 0f;

        public void OnGrab()
        {
            Body.Grab();
            Body.ClingOnContact = false;
            Start(Sys(SystemBehaviors.Held));
        }

        public void OnDrag(Vec2 footPosition) => Body.SetHeldPosition(footPosition);

        public void OnRelease(Vec2 velocity)
        {
            bool thrown = velocity.Length >= ThrowSpeedThreshold;
            Body.ClingOnContact = thrown;
            Body.Release(velocity);
            if (Math.Abs(velocity.X) > 1f) Facing = velocity.X > 0 ? 1 : -1;
            Start(Sys(thrown ? SystemBehaviors.Thrown : SystemBehaviors.Fall));
        }

        public void OnClick()
        {
            IdleSeconds = 0;
            if (Body.State != BodyState.Grounded) return;
            Body.MoveSpeed = 0;
            Start(Sys(SystemBehaviors.ClickReact));
        }

        public void OnDoubleClick()
        {
            IdleSeconds = 0;
            if (Body.State != BodyState.Grounded) return;
            Body.MoveSpeed = 0;
            Start(Sys(SystemBehaviors.DoubleClickReact));
        }

        /// <summary>컨텍스트 메뉴 등에서 행동 강제. 현재 상태와 맞지 않으면 false.</summary>
        public bool ForceBehavior(string id)
        {
            var d = Selector.Get(id) ?? SystemBehaviors.CreateDefault(id);
            if (d == null || d.RequiredState != Body.State) return false;
            Start(d);
            return true;
        }

        // ---------------- 진행 ----------------

        public void Tick(float dt, IWorld world)
        {
            Time += dt;
            IdleSeconds += dt;
            if (Paused)
            {
                Body.MoveSpeed = 0;
                LastEvents = BodyEvents.None;
                return;
            }

            if (Current == null) SelectNext(world);

            var events = Body.Update(dt, world);
            LastEvents = events;

            if (Body.State == BodyState.Held)
            {
                if (Current == null || Current.RequiredState != BodyState.Held) Start(Sys(SystemBehaviors.Held));
                UpdateAnimation();
                return;
            }

            // --- 환경 인터럽트 (최우선) ---
            if ((events & BodyEvents.Landed) != 0)
            {
                Body.ClingOnContact = false;
                Start(Sys(SystemBehaviors.Land));
            }
            else if (Body.State == BodyState.Airborne && Current.RequiredState != BodyState.Airborne)
            {
                Start(Sys(SystemBehaviors.Fall));
            }
            else if (Current.RequiredState != Body.State && !(Current.Id == SystemBehaviors.Land && Body.State == BodyState.Grounded))
            {
                if (Body.State != BodyState.Airborne) Body.ClingOnContact = false;
                SelectNext(world);
            }

            // --- 현재 행동 실행 ---
            Remaining -= dt;
            if (Remaining <= 0f && Body.State != BodyState.Airborne)
            {
                SelectNext(world);
            }
            else
            {
                RunMotion(world, events);
            }

            UpdateAnimation();
        }

        void RunMotion(IWorld world, BodyEvents events)
        {
            if (Current == null) return;
            switch (Current.Motion)
            {
                case MotionType.Walk:
                {
                    if (Body.State != BodyState.Grounded) break;
                    if ((events & BodyEvents.BlockedByWall) != 0) { Body.MoveSpeed = 0; SelectNext(world); return; }
                    float edge = Body.DistanceToEdge(world, Facing, 200f);
                    if (edge < 6f && !_edgeDecisionMade)
                    {
                        _edgeDecisionMade = true;
                        _walkOffEdge = Selector.Random.NextDouble() >= StopAtEdgeChance;
                    }
                    if (edge < 6f && !_walkOffEdge)
                    {
                        Body.MoveSpeed = 0;
                        SelectNext(world);
                        return;
                    }
                    Body.MoveSpeed = Facing * Speed;
                    break;
                }
                case MotionType.Climb:
                    if (Body.State == BodyState.WallAttached)
                    {
                        if ((events & BodyEvents.ReachedWallEnd) != 0 && Speed > 0) { Body.MoveSpeed = 0; SelectNext(world); return; }
                        Body.MoveSpeed = Speed;
                    }
                    break;
                case MotionType.Crawl:
                    if (Body.State == BodyState.CeilingAttached)
                    {
                        if ((events & BodyEvents.ReachedCeilingEnd) != 0) Facing = -Facing;
                        Body.MoveSpeed = Facing * Speed;
                    }
                    break;
                default:
                    Body.MoveSpeed = 0;
                    break;
            }
        }

        public BehaviorContext BuildContext(IWorld world)
        {
            var ctx = new BehaviorContext
            {
                State = Body.State,
                Surface = Body.Surface?.Type,
                IdleSeconds = IdleSeconds,
                Time = Time,
            };
            if (Body.State == BodyState.Grounded)
            {
                ctx.Set(ConditionFlags.BlockedLeft, IsBlocked(world, -1));
                ctx.Set(ConditionFlags.BlockedRight, IsBlocked(world, 1));
                float el = Body.DistanceToEdge(world, -1, 300f), er = Body.DistanceToEdge(world, 1, 300f);
                ctx.Set(ConditionFlags.EdgeLeft, el < 30f);
                ctx.Set(ConditionFlags.EdgeRight, er < 30f);
                ctx.Set(ConditionFlags.NearEdge, Math.Min(el, er) < 60f);
                ctx.Set(ConditionFlags.WallAdjacent, ClimbableWallDir(world) != 0);
                _jumpTarget = FindJumpTarget(world);
                ctx.Set(ConditionFlags.JumpTarget, _jumpTarget.HasValue);
                ctx.Set(ConditionFlags.OnWindow, Body.Surface?.Type == SurfaceType.WindowTop);
            }
            else if (Body.State == BodyState.WallAttached && Body.Surface != null)
            {
                ctx.Set(ConditionFlags.AtWallTop, Body.Position.Y - Body.Height <= Body.Surface.Min + 2f);
                ctx.Set(ConditionFlags.AtWallBottom, Body.Position.Y >= Body.Surface.Max - 2f);
            }
            ctx.Set(ConditionFlags.Sleepy, IdleSeconds > 120f);
            return ctx;
        }

        bool IsBlocked(IWorld world, int dir)
        {
            if (Body.DistanceToWall(world, dir, 12f, out _) <= 12f) return true;
            // 모니터 바깥(가상 데스크톱 경계)
            var probe = new Vec2(Body.Position.X + dir * (Body.Width * 0.5f + 12f), Body.Position.Y - 1f);
            foreach (var m in world.Monitors) if (m.Bounds.ContainsInclusive(probe)) return false;
            return true;
        }

        int ClimbableWallDir(IWorld world)
        {
            for (int dir = -1; dir <= 1; dir += 2)
            {
                var w = Body.FindAdjacentWall(world, dir, 8f);
                if (w != null && w.Min < Body.Position.Y - Body.Height) return dir;
            }
            return 0;
        }

        /// <summary>점프로 올라설 수 있는 더 높은 지면을 찾는다(창 옆에서 비스듬히 올라 윗면 가장자리에 착지).</summary>
        Vec2? FindJumpTarget(IWorld world)
        {
            float x = Body.Position.X, y = Body.Position.Y, halfW = Body.Width * 0.5f;
            Vec2? best = null;
            int count = 0;
            foreach (var s in world.Surfaces)
            {
                if (!s.IsGround || s == Body.Surface) continue;
                float dy = y - s.Fixed;
                if (dy < 40f || dy > MaxJumpHeight) continue;
                if (s.Length < Body.Width * 1.5f) continue;
                // 표면 바로 아래에 있으면 머리를 찧으므로 제외
                if (x > s.Min - halfW && x < s.Max + halfW) continue;
                float landX = x < s.Min ? s.Min + Body.Width * 0.8f : s.Max - Body.Width * 0.8f;
                if (Math.Abs(landX - x) > MaxJumpDistance) continue;
                count++;
                if (Selector.Random.Next(count) == 0) best = new Vec2(landX, s.Fixed);
            }
            return best;
        }

        Vec2 ComputeJumpVelocity(Vec2 target)
        {
            float g = Body.P.Gravity;
            float rise = Body.Position.Y - target.Y + 70f;
            float vy = -(float)Math.Sqrt(2f * g * Math.Max(rise, 10f));
            float tUp = -vy / g;
            float tDown = (float)Math.Sqrt(2f * 70f / g);
            float t = tUp + tDown;
            float vx = (target.X - Body.Position.X) / t;
            vx *= 1f + Body.P.AirDrag * t * 0.5f;
            return new Vec2(vx, vy);
        }

        void SelectNext(IWorld world)
        {
            var ctx = BuildContext(world);
            var next = Selector.Select(ctx, Current);
            if (next == null)
            {
                // 후보 없음: 붙어 있으면 손을 놓고, 지면이면 대기
                if (Body.State == BodyState.WallAttached || Body.State == BodyState.CeilingAttached)
                {
                    Body.Drop();
                    Start(Sys(SystemBehaviors.Fall));
                }
                else if (Body.State == BodyState.Airborne) Start(Sys(SystemBehaviors.Fall));
                else Start(Sys(SystemBehaviors.Idle));
                return;
            }
            Start(next, world);
        }

        void Start(BehaviorDef def, IWorld world = null)
        {
            Current = def;
            BehaviorSerial++;
            Selector.MarkUsed(def, Time);
            Remaining = Selector.NextFloat(def.DurationMin, def.DurationMax);
            float speed = Selector.NextFloat(def.SpeedMin, def.SpeedMax);
            Speed = def.Motion == MotionType.Walk || def.Motion == MotionType.Crawl ? Math.Abs(speed) * Math.Max(0.5f, (float)Math.Sqrt(Selector.Activity)) : speed;
            _edgeDecisionMade = false;
            _walkOffEdge = false;

            switch (def.Direction)
            {
                case DirectionMode.Left: Facing = -1; break;
                case DirectionMode.Right: Facing = 1; break;
                case DirectionMode.Opposite: Facing = -Facing; break;
                case DirectionMode.Random: Facing = Selector.Random.Next(2) == 0 ? -1 : 1; break;
                case DirectionMode.TowardWall:
                    if (world != null)
                    {
                        int w = Body.State == BodyState.WallAttached ? WallDir() : ClimbableWallDir(world);
                        if (w != 0) Facing = w;
                    }
                    break;
            }
            if (Body.State == BodyState.WallAttached) Facing = WallDir();

            if (world == null) { Body.MoveSpeed = 0; return; }

            switch (def.Motion)
            {
                case MotionType.Walk:
                    if (Body.State == BodyState.Grounded && IsBlocked(world, Facing)) Facing = -Facing;
                    break;
                case MotionType.Climb:
                    if (Body.State == BodyState.Grounded)
                    {
                        if (!Body.TryAttachWall(world, Facing))
                        {
                            Current = Sys(SystemBehaviors.Idle);
                            Remaining = 1f;
                            break;
                        }
                        Facing = WallDir();
                    }
                    break;
                case MotionType.Jump:
                    if (Body.State == BodyState.Grounded && _jumpTarget.HasValue)
                    {
                        var t = _jumpTarget.Value;
                        Facing = t.X >= Body.Position.X ? 1 : -1;
                        Body.Jump(ComputeJumpVelocity(t));
                        Current = Sys(SystemBehaviors.Fall);
                        Remaining = Current.DurationMax;
                    }
                    break;
                case MotionType.Drop:
                    Body.Drop();
                    Current = Sys(SystemBehaviors.Fall);
                    Remaining = Current.DurationMax;
                    break;
            }
            Body.MoveSpeed = 0;
        }

        int WallDir() => Body.Surface?.Type == SurfaceType.WallLeft ? -1 : 1;

        void UpdateAnimation()
        {
            if (Current == null) return;
            var key = Current.AnimationKey;
            AnimationSpeed = 1f;
            if (Body.State == BodyState.Airborne)
            {
                if (Current.Id == SystemBehaviors.Thrown) key = AnimKeys.Thrown;
                else key = Body.Velocity.Y < -150f ? AnimKeys.Jump : AnimKeys.Fall;
            }
            else if (Current.Motion == MotionType.Walk || Current.Motion == MotionType.Crawl || Current.Motion == MotionType.Climb)
            {
                // 키 높이 기준 보폭: 기본 클립은 초당 (키 * 0.45) px 이동에 맞춰져 있다고 가정
                float nominal = Math.Max(1f, Body.Height * (Current.Motion == MotionType.Walk ? 0.45f : 0.3f));
                float s = Math.Abs(Body.MoveSpeed);
                AnimationSpeed = s < 1f ? 0.0f : MathUtil.Clamp(s / nominal, 0.5f, 2.5f);
                if (Current.Motion == MotionType.Walk && s < 1f) key = AnimKeys.Idle;
            }
            AnimationKey = key;
        }
    }
}
