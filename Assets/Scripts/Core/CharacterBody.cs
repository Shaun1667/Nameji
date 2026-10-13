using System;
using System.Collections.Generic;

namespace Shimeji.Core
{
    public enum BodyState { Grounded, Airborne, WallAttached, CeilingAttached, Held }

    [Flags]
    public enum BodyEvents
    {
        None = 0,
        Landed = 1 << 0,
        StartedFalling = 1 << 1,
        BlockedByWall = 1 << 2,
        ClimbedOntoTop = 1 << 3,
        ReachedCeiling = 1 << 4,
        Respawned = 1 << 5,
        HitCeilingInAir = 1 << 6,
        ReachedWallBottom = 1 << 7,
        AttachedToWall = 1 << 8,
        BouncedOffWall = 1 << 9,
        ReachedWallEnd = 1 << 10,
        ReachedCeilingEnd = 1 << 11,
    }

    public sealed class PhysicsParams
    {
        public float Gravity = 2200f;          // px/s²
        public float MaxFallSpeed = 2000f;     // px/s
        public float AirDrag = 0.35f;          // 수평 속도 감쇠 (1/s)
        public float StepHeight = 40f;         // 걸어서 오를/내릴 수 있는 단차
        public float MaxThrowSpeed = 3200f;
        public float WallBounce = 0.35f;
        public float OutOfBoundsMargin = 400f;
        public float MaxAirTime = 12f;         // 이보다 오래 떠 있으면 끼었다고 보고 복구
        public float WallAttachTolerance = 6f;
        public float FixedStep = 1f / 60f;
    }

    /// <summary>
    /// 화면 픽셀 공간의 캐릭터 몸체. Position은 발바닥 중앙.
    /// 순수 C#: IWorld만 보고 계산하며 렌더링은 바깥에서 Position/State를 읽어 반영한다.
    /// </summary>
    public sealed class CharacterBody
    {
        public Vec2 Position;
        public Vec2 Velocity;
        public float Width = 60f;
        public float Height = 160f;
        public readonly PhysicsParams P;

        /// <summary>이동 의도(px/s). Grounded/Ceiling: +오른쪽, Wall: +위쪽.</summary>
        public float MoveSpeed;
        /// <summary>공중에서 벽/천장에 닿으면 붙을지(던지기 후 벽 착지 등).</summary>
        public bool ClingOnContact;

        public BodyState State { get; private set; } = BodyState.Airborne;
        public Surface Surface { get; private set; }
        /// <summary>마지막으로 막힌 벽 (없으면 null = 모니터 경계).</summary>
        public Surface BlockingWall { get; private set; }
        public float AirTime { get; private set; }

        float _accumulator;
        bool _hasOwnerRect;
        RectPx _ownerRect;
        BodyEvents _events;

        public CharacterBody(PhysicsParams p = null) { P = p ?? new PhysicsParams(); }

        float HalfW => Width * 0.5f;

        /// <summary>고정 스텝으로 진행. 이번 호출 동안 발생한 이벤트를 반환.</summary>
        public BodyEvents Update(float dt, IWorld world)
        {
            _events = BodyEvents.None;
            _accumulator += Math.Min(dt, 0.25f);
            while (_accumulator >= P.FixedStep)
            {
                Simulate(P.FixedStep, world);
                _accumulator -= P.FixedStep;
            }
            return _events;
        }

        public void Simulate(float dt, IWorld world)
        {
            switch (State)
            {
                case BodyState.Held: return;
                case BodyState.Grounded: StepGrounded(dt, world); break;
                case BodyState.Airborne: StepAirborne(dt, world); break;
                case BodyState.WallAttached: StepWall(dt, world); break;
                case BodyState.CeilingAttached: StepCeiling(dt, world); break;
            }
            Recover(world);
        }

        // ---------------- 외부 명령 ----------------

        public void Grab()
        {
            State = BodyState.Held;
            Surface = null;
            Velocity = Vec2.Zero;
            MoveSpeed = 0;
            AirTime = 0;
        }

        public void SetHeldPosition(Vec2 p)
        {
            if (State == BodyState.Held) Position = p;
        }

        public void Release(Vec2 velocity)
        {
            float len = velocity.Length;
            if (len > P.MaxThrowSpeed) velocity = velocity * (P.MaxThrowSpeed / len);
            BecomeAirborne(velocity);
        }

        public void Jump(Vec2 velocity)
        {
            BecomeAirborne(velocity);
        }

        /// <summary>붙어 있던 표면에서 손을 놓고 떨어진다.</summary>
        public void Drop()
        {
            if (State == BodyState.Held) return;
            BecomeAirborne(Vec2.Zero);
        }

        /// <summary>디버그/복원용: 해당 위치에서 낙하 시작.</summary>
        public void PlaceAt(Vec2 p)
        {
            Position = p;
            BecomeAirborne(Vec2.Zero);
        }

        /// <summary>지면 위에서 진행 방향(dir=±1)에 맞닿은 벽에 붙는다.</summary>
        public bool TryAttachWall(IWorld world, int dir)
        {
            if (State != BodyState.Grounded && State != BodyState.Airborne) return false;
            var wall = FindAdjacentWall(world, dir, P.WallAttachTolerance);
            if (wall == null) return false;
            AttachWall(wall);
            return true;
        }

        public Surface FindAdjacentWall(IWorld world, int dir, float tolerance)
        {
            var type = dir > 0 ? SurfaceType.WallRight : SurfaceType.WallLeft;
            float side = Position.X + dir * HalfW;
            Surface best = null;
            float bestD = float.MaxValue;
            foreach (var s in world.Surfaces)
            {
                if (s.Type != type) continue;
                float d = Math.Abs(s.Fixed - side);
                if (d > tolerance) continue;
                // 몸 높이 구간과 겹쳐야 하고, 벽 위쪽으로 오를 여지가 있어야 한다.
                if (!(s.Min < Position.Y - 1f && s.Max > Position.Y - Height + 1f)) continue;
                if (d < bestD) { bestD = d; best = s; }
            }
            return best;
        }

        void AttachWall(Surface wall)
        {
            State = BodyState.WallAttached;
            Surface = wall;
            Velocity = Vec2.Zero;
            AirTime = 0;
            Position.X = wall.Type == SurfaceType.WallRight ? wall.Fixed - HalfW : wall.Fixed + HalfW;
            Position.Y = MathUtil.Clamp(Position.Y, wall.Min + Height * 0.5f, wall.Max);
            RememberOwner();
            _events |= BodyEvents.AttachedToWall;
        }

        void AttachGround(Surface s, float x)
        {
            State = BodyState.Grounded;
            Surface = s;
            Position = new Vec2(x, s.Fixed);
            Velocity = Vec2.Zero;
            AirTime = 0;
            RememberOwner();
        }

        void AttachCeiling(Surface s, float x)
        {
            State = BodyState.CeilingAttached;
            Surface = s;
            Position = new Vec2(x, s.Fixed + Height);
            Velocity = Vec2.Zero;
            AirTime = 0;
            RememberOwner();
            _events |= BodyEvents.ReachedCeiling;
        }

        void BecomeAirborne(Vec2 velocity)
        {
            if (State != BodyState.Airborne) AirTime = 0;
            State = BodyState.Airborne;
            Surface = null;
            Velocity = velocity;
            _hasOwnerRect = false;
            _events |= BodyEvents.StartedFalling;
        }

        void RememberOwner()
        {
            _hasOwnerRect = false;
        }

        /// <summary>표면 소유 창이 움직였으면 같이 이동한다. 소유자가 사라졌으면 false.</summary>
        bool FollowOwner(IWorld world)
        {
            if (Surface == null || Surface.OwnerId == 0) return true;
            if (!world.TryGetOwnerRect(Surface.OwnerId, out var rect)) return false;
            if (_hasOwnerRect)
            {
                float dx = rect.Left - _ownerRect.Left;
                float dy = rect.Top - _ownerRect.Top;
                // 크기 변경 시 해당 변의 이동량을 쓴다.
                if (Surface.Type == SurfaceType.WallLeft) dx = rect.Right - _ownerRect.Right;
                if (Surface.Type == SurfaceType.Ceiling) dy = rect.Bottom - _ownerRect.Bottom;
                Position = new Vec2(Position.X + dx, Position.Y + dy);
            }
            _ownerRect = rect;
            _hasOwnerRect = true;
            return true;
        }

        /// <summary>현재 붙은 표면을 최신 월드에서 다시 찾는다(같은 소유자/종류, 위치 포함).</summary>
        Surface Reresolve(IWorld world, float along, float fixedCoord)
        {
            if (Surface == null) return null;
            foreach (var s in world.Surfaces)
            {
                if (s.Type != Surface.Type || s.OwnerId != Surface.OwnerId) continue;
                if (Math.Abs(s.Fixed - fixedCoord) > 1f) continue;
                if (!s.ContainsAlong(along, 0.5f)) continue;
                return s;
            }
            return null;
        }

        // ---------------- 상태별 스텝 ----------------

        void StepGrounded(float dt, IWorld world)
        {
            if (!FollowOwner(world)) { BecomeAirborne(Vec2.Zero); return; }

            var cur = Reresolve(world, Position.X, Position.Y);
            if (cur == null)
            {
                // 표면이 가려졌거나 사라짐 → 이어지는 표면(단차 이내)이 있으면 그쪽으로, 아니면 낙하
                var alt = FindGroundNear(world, Position.X, Position.Y, 2f);
                if (alt == null) { BecomeAirborne(Vec2.Zero); return; }
                cur = alt;
                Position.Y = alt.Fixed;
            }
            Surface = cur;

            float dx = MoveSpeed * dt;
            float newX = Position.X + dx;
            BlockingWall = null;

            if (dx != 0)
            {
                int dir = Math.Sign(dx);
                var wall = FindWallCrossing(world, Position.X, newX, Position.Y, dir);
                if (wall != null)
                {
                    newX = dir > 0 ? wall.Fixed - HalfW : wall.Fixed + HalfW;
                    if (dir > 0 ? newX < Position.X : newX > Position.X) newX = Position.X;
                    BlockingWall = wall;
                    _events |= BodyEvents.BlockedByWall;
                }
                else if (!InsideAnyMonitor(world, new Vec2(newX + dir * HalfW, Position.Y - 1f)))
                {
                    newX = Position.X;
                    _events |= BodyEvents.BlockedByWall;
                }
            }

            if (cur.ContainsAlong(newX, 0.5f))
            {
                Position.X = newX;
            }
            else
            {
                var next = FindGroundNear(world, newX, Position.Y, P.StepHeight);
                if (next != null)
                {
                    Surface = next;
                    Position = new Vec2(newX, next.Fixed);
                    _hasOwnerRect = false;
                }
                else
                {
                    Position.X = newX;
                    BecomeAirborne(new Vec2(MoveSpeed, 0));
                    return;
                }
            }
            Velocity = new Vec2(MoveSpeed, 0);
        }

        void StepAirborne(float dt, IWorld world)
        {
            AirTime += dt;
            var v = Velocity;
            v.Y = Math.Min(v.Y + P.Gravity * dt, P.MaxFallSpeed);
            v.X *= Math.Max(0f, 1f - P.AirDrag * dt);
            var prev = Position;
            var next = new Vec2(prev.X + v.X * dt, prev.Y + v.Y * dt);

            // 천장 (위로 이동 중 머리가 통과)
            if (v.Y < 0)
            {
                float prevHead = prev.Y - Height, newHead = next.Y - Height;
                Surface hit = null;
                foreach (var s in world.Surfaces)
                {
                    if (s.Type != SurfaceType.Ceiling) continue;
                    if (prevHead >= s.Fixed - 0.01f && newHead < s.Fixed && s.ContainsAlong(next.X))
                        if (hit == null || s.Fixed > hit.Fixed) hit = s;
                }
                if (hit != null)
                {
                    if (ClingOnContact) { AttachCeiling(hit, next.X); return; }
                    next.Y = hit.Fixed + Height;
                    v.Y = 0;
                    _events |= BodyEvents.HitCeilingInAir;
                }
            }

            // 벽
            if (v.X != 0)
            {
                int dir = Math.Sign(v.X);
                var wall = FindWallCrossing(world, prev.X, next.X, next.Y, dir);
                if (wall != null)
                {
                    next.X = dir > 0 ? wall.Fixed - HalfW : wall.Fixed + HalfW;
                    if (ClingOnContact && wall.Max - wall.Min > Height * 0.75f)
                    {
                        Position = next;
                        AttachWall(wall);
                        return;
                    }
                    v.X = -v.X * P.WallBounce;
                    _events |= BodyEvents.BouncedOffWall;
                }
            }

            // 바닥/창 윗면 (아래로 이동 중 발이 통과)
            if (v.Y > 0)
            {
                Surface ground = null;
                foreach (var s in world.Surfaces)
                {
                    if (!s.IsGround) continue;
                    if (prev.Y <= s.Fixed + 0.01f && next.Y >= s.Fixed && s.ContainsAlong(next.X))
                        if (ground == null || s.Fixed < ground.Fixed) ground = s;
                }
                if (ground != null)
                {
                    AttachGround(ground, next.X);
                    MoveSpeed = 0;
                    _events |= BodyEvents.Landed;
                    return;
                }
            }

            Position = next;
            Velocity = v;
        }

        void StepWall(float dt, IWorld world)
        {
            if (!FollowOwner(world)) { BecomeAirborne(Vec2.Zero); return; }
            var wall = Surface;
            float wallX = wall.Type == SurfaceType.WallRight ? Position.X + HalfW : Position.X - HalfW;
            var cur = Reresolve(world, Position.Y - Height * 0.5f, wallX);
            if (cur == null) { BecomeAirborne(Vec2.Zero); return; }
            Surface = wall = cur;
            int dir = wall.Type == SurfaceType.WallRight ? 1 : -1;

            float newY = Position.Y - MoveSpeed * dt;
            Velocity = new Vec2(0, -MoveSpeed);

            // 위 끝
            if (MoveSpeed > 0 && newY - Height * 0.5f <= wall.Min)
            {
                // 벽 위가 창 윗면/바닥이면 올라선다.
                float topX = wall.Fixed + dir * HalfW * 0.8f;
                var top = FindGroundNear(world, topX, wall.Min, 2f);
                if (top != null)
                {
                    AttachGround(top, wall.Fixed + dir * HalfW);
                    _events |= BodyEvents.ClimbedOntoTop;
                    return;
                }
                if (newY - Height <= wall.Min)
                {
                    var ceil = FindCeilingAt(world, Position.X, wall.Min, 2f);
                    if (ceil != null) { AttachCeiling(ceil, Position.X); return; }
                    newY = wall.Min + Height;
                    _events |= BodyEvents.ReachedWallEnd;
                }
            }

            // 아래 끝
            if (MoveSpeed < 0 && newY >= wall.Max)
            {
                var ground = FindGroundNear(world, Position.X, wall.Max, 2f);
                if (ground != null)
                {
                    AttachGround(ground, Position.X);
                    _events |= BodyEvents.ReachedWallBottom;
                    return;
                }
                Position.Y = wall.Max;
                BecomeAirborne(Vec2.Zero);
                return;
            }

            Position.Y = newY;
        }

        void StepCeiling(float dt, IWorld world)
        {
            if (!FollowOwner(world)) { BecomeAirborne(Vec2.Zero); return; }
            var cur = Reresolve(world, Position.X, Position.Y - Height);
            if (cur == null) { BecomeAirborne(Vec2.Zero); return; }
            Surface = cur;
            float newX = Position.X + MoveSpeed * dt;
            Velocity = new Vec2(MoveSpeed, 0);
            if (!cur.ContainsAlong(newX))
            {
                float end = newX > cur.Max ? cur.Max : cur.Min;
                // 끝이 벽(모니터 가장자리)이면 거기서 멈춘다. 아니면(창 아랫면 끝) 떨어진다.
                bool wallAtEnd = false;
                foreach (var s in world.Surfaces)
                    if (s.IsWall && Math.Abs(s.Fixed - end) < 2f && s.ContainsAlong(cur.Fixed + 1f)) { wallAtEnd = true; break; }
                if (wallAtEnd)
                {
                    newX = MathUtil.Clamp(newX, cur.Min + HalfW, cur.Max - HalfW);
                    _events |= BodyEvents.ReachedCeilingEnd | BodyEvents.BlockedByWall;
                }
                else
                {
                    Position.X = newX;
                    BecomeAirborne(Vec2.Zero);
                    return;
                }
            }
            Position = new Vec2(newX, cur.Fixed + Height);
        }

        // ---------------- 질의 ----------------

        Surface FindWallCrossing(IWorld world, float fromX, float toX, float footY, int dir)
        {
            var type = dir > 0 ? SurfaceType.WallRight : SurfaceType.WallLeft;
            float fromSide = fromX + dir * HalfW, toSide = toX + dir * HalfW;
            Surface best = null;
            foreach (var s in world.Surfaces)
            {
                if (s.Type != type) continue;
                bool crosses = dir > 0
                    ? s.Fixed >= fromSide - 0.01f && s.Fixed < toSide
                    : s.Fixed <= fromSide + 0.01f && s.Fixed > toSide;
                if (!crosses) continue;
                // 몸 높이 구간과 겹쳐야 막힌다.
                if (!(s.Min < footY - 1f && s.Max > footY - Height + 1f)) continue;
                if (best == null || (dir > 0 ? s.Fixed < best.Fixed : s.Fixed > best.Fixed)) best = s;
            }
            return best;
        }

        /// <summary>x 위치에서 y와의 차이가 maxDelta 이내인 지면 표면 (가장 가까운 것).</summary>
        public static Surface FindGroundNear(IWorld world, float x, float y, float maxDelta)
        {
            Surface best = null;
            float bestD = float.MaxValue;
            foreach (var s in world.Surfaces)
            {
                if (!s.IsGround || !s.ContainsAlong(x, 0.5f)) continue;
                float d = Math.Abs(s.Fixed - y);
                if (d <= maxDelta && d < bestD) { best = s; bestD = d; }
            }
            return best;
        }

        static Surface FindCeilingAt(IWorld world, float x, float y, float maxDelta)
        {
            foreach (var s in world.Surfaces)
                if (s.Type == SurfaceType.Ceiling && Math.Abs(s.Fixed - y) <= maxDelta && s.ContainsAlong(x, 0.5f)) return s;
            return null;
        }

        static bool InsideAnyMonitor(IWorld world, Vec2 p)
        {
            foreach (var m in world.Monitors) if (m.Bounds.ContainsInclusive(p)) return true;
            return false;
        }

        /// <summary>지면에서 진행 방향 끝(낭떠러지)까지의 거리. 단차 이내로 이어지는 표면은 연결해서 본다.</summary>
        public float DistanceToEdge(IWorld world, int dir, float maxDistance = 2000f)
        {
            if (State != BodyState.Grounded || Surface == null) return 0f;
            var s = Surface;
            float x = Position.X, y = Position.Y;
            float travelled = 0f;
            for (int guard = 0; guard < 8; guard++)
            {
                float end = dir > 0 ? s.Max : s.Min;
                travelled += Math.Abs(end - x);
                if (travelled >= maxDistance) return maxDistance;
                var next = FindGroundNear(world, end + dir * 1.5f, y, P.StepHeight);
                if (next == null || next == s) return travelled;
                x = end; y = next.Fixed; s = next;
            }
            return travelled;
        }

        /// <summary>지면 위에서 진행 방향으로 maxDistance 이내에 막는 벽까지의 거리 (없으면 +∞).</summary>
        public float DistanceToWall(IWorld world, int dir, float maxDistance, out Surface wall)
        {
            wall = FindWallCrossing(world, Position.X, Position.X + dir * maxDistance, Position.Y, dir);
            if (wall == null)
            {
                // 모니터 경계도 벽으로 본다.
                return float.PositiveInfinity;
            }
            return Math.Abs(wall.Fixed - (Position.X + dir * HalfW));
        }

        // ---------------- 복구 ----------------

        void Recover(IWorld world)
        {
            if (State == BodyState.Held || world.Monitors.Count == 0) return;
            var center = new Vec2(Position.X, Position.Y - Height * 0.5f);
            bool inside = false;
            foreach (var m in world.Monitors)
                if (m.Bounds.Expanded(P.OutOfBoundsMargin).Contains(center)) { inside = true; break; }
            // 작업영역이 바뀌어(작업표시줄 이동 등) 바닥 아래에 있게 되면 바닥으로 올린다.
            if (State == BodyState.Airborne && Velocity.Y >= 0)
            {
                var mon = DesktopWorld.FindMonitor(world.Monitors, Position);
                if (mon != null && Position.Y > mon.WorkArea.Bottom + 1f)
                {
                    var floor = FindGroundNear(world, Position.X, mon.WorkArea.Bottom, 1f);
                    if (floor != null)
                    {
                        AttachGround(floor, Position.X);
                        _events |= BodyEvents.Landed;
                        return;
                    }
                }
            }
            bool stuckInAir = State == BodyState.Airborne && AirTime > P.MaxAirTime;
            if (!inside || stuckInAir || float.IsNaN(Position.X) || float.IsNaN(Position.Y))
                Respawn(world);
        }

        public void Respawn(IWorld world)
        {
            var m = DesktopWorld.FindMonitor(world.Monitors, Position) ?? DesktopWorld.PrimaryOrFirst(world.Monitors);
            if (m == null) return;
            var w = m.WorkArea;
            float x = float.IsNaN(Position.X) ? w.Center.X : MathUtil.Clamp(Position.X, w.Left + Width, w.Right - Width);
            Position = new Vec2(x, w.Top + Height + 4f);
            BecomeAirborne(Vec2.Zero);
            _events |= BodyEvents.Respawned;
        }
    }
}
