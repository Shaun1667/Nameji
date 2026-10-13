using System;
using Shimeji.Core;
using UnityEngine;

namespace Shimeji.Characters
{
    /// <summary>
    /// 캐릭터 1개: 순수 C# 몸체/두뇌(픽셀 공간)와 Unity 표현(Transform/애니메이터)을 잇는다.
    /// 업데이트 순서를 보장하기 위해 Unity Update 대신 앱이 Step을 호출한다.
    /// </summary>
    public sealed class CharacterActor : MonoBehaviour
    {
        static int _nextId;

        public int RuntimeId { get; private set; }
        public string Id { get; private set; }
        /// <summary>인스턴스 키 (원본은 폴더 이름, 복제본은 "폴더#n").</summary>
        public string InstanceKey { get; set; }
        /// <summary>Characters/ 아래 폴더 이름.</summary>
        public string FolderName { get; set; }
        public CharacterConfig Config { get; private set; }
        public CharacterRig Rig { get; private set; }
        public CharacterAnimator Anim { get; private set; }
        public CharacterBody Body { get; private set; }
        public CharacterBrain Brain { get; private set; }
        public Transform Visual { get; private set; }
        public Transform Oriented { get; private set; }
        /// <summary>트레이/설정에서 쓰는 사용자 크기 배율.</summary>
        public float UserScale { get; private set; } = 1f;
        public float BaseHeightPx { get; private set; }
        public float Depth { get; set; }

        LoadedModel _loaded;
        float _ppu;
        float _yaw;
        float _yawVelocity;
        float _tilt, _tiltVelocity;
        Vec2 _grabOffset;      // 발 위치 - 커서 (드래그 시작 시)
        Vec2 _lastHeldPos;
        float _heldVx;
        bool _destroyed;

        /// <summary>앞을 보는 상태 키(앉기/수면 등)는 카메라 쪽으로 몸을 돌린다.</summary>
        static readonly string[] FrontKeys =
        {
            AnimKeys.IdleLookAround, AnimKeys.Sit, AnimKeys.SitEdge, AnimKeys.Sleep, AnimKeys.ClickReact,
            AnimKeys.Wave, AnimKeys.Dance, AnimKeys.HangCeiling, AnimKeys.Dragged, AnimKeys.Land,
        };

        public void Initialize(string id, CharacterConfig cfg, CharacterRig rig, CharacterAnimator anim,
            Transform visual, Transform oriented, LoadedModel loaded, Vector3 sizeWorld, float ppu)
        {
            RuntimeId = ++_nextId;
            Id = id;
            Config = cfg;
            Rig = rig;
            Anim = anim;
            Visual = visual;
            Oriented = oriented;
            _loaded = loaded;
            _ppu = ppu;
            BaseHeightPx = cfg.HeightPixels;
            Body = new CharacterBody { Height = cfg.HeightPixels, Width = Mathf.Clamp(Mathf.Max(sizeWorld.x, sizeWorld.z) * ppu * 0.6f, cfg.HeightPixels * 0.3f, cfg.HeightPixels * 0.6f) };
            _yaw = cfg.FacingYawDeg + cfg.ViewAngleDeg;
            Visual.localRotation = Quaternion.Euler(0, _yaw, 0);
        }

        public void AttachBrain(BehaviorSelector selector)
        {
            Brain = new CharacterBrain(Body, selector);
        }

        public void SetUserScale(float scale)
        {
            UserScale = Mathf.Clamp(scale, 0.25f, 4f);
            Visual.localScale = Vector3.one * UserScale;
            float h = BaseHeightPx * UserScale;
            float ratio = Body.Width / Mathf.Max(1f, Body.Height);
            Body.Height = h;
            Body.Width = h * ratio;
        }

        // ---------------- 입력 ----------------

        public void BeginDrag(Vec2 cursor)
        {
            _grabOffset = Body.Position - cursor;
            // 잡은 지점이 너무 아래(발)면 매달린 모습이 어색하므로 최소 몸 중간 위를 잡은 것으로 본다.
            _grabOffset.Y = Mathf.Max(_grabOffset.Y, Body.Height * 0.45f);
            _grabOffset.X = Mathf.Clamp(_grabOffset.X, -Body.Width * 0.5f, Body.Width * 0.5f);
            Brain.OnGrab();
            Brain.OnDrag(cursor + _grabOffset);
            _lastHeldPos = Body.Position;
        }

        public void Drag(Vec2 cursor) => Brain.OnDrag(cursor + _grabOffset);

        public void EndDrag(Vec2 velocity) => Brain.OnRelease(velocity);

        public Vec2 GrabPoint => Body.Position - _grabOffset;

        // ---------------- 진행 ----------------

        public void Step(float dt, IWorld world, ScreenSpace space, Vector3? lookTargetWorld)
        {
            if (_destroyed) return;
            Brain.Tick(dt, world);
            Anim.Play(Brain.AnimationKey, Brain.AnimationSpeed);
            Anim.LookTarget = lookTargetWorld;
            ApplyTransform(dt, space);
        }

        void ApplyTransform(float dt, ScreenSpace space)
        {
            var foot = space.DesktopToWorld(Body.Position);

            // --- 방향 (0.2초 보간) ---
            float baseYaw = Config.FacingYawDeg;
            float targetYaw;
            var key = Brain.AnimationKey;
            int f = Brain.Facing;
            if (Body.State == BodyState.WallAttached)
                targetYaw = f > 0 ? baseYaw : -baseYaw;
            else if (Array.IndexOf(FrontKeys, key) >= 0)
                targetYaw = 180f - f * 25f;
            else
                targetYaw = f > 0 ? baseYaw + Config.ViewAngleDeg : -baseYaw - Config.ViewAngleDeg;
            _yaw = Mathf.SmoothDampAngle(_yaw, targetYaw, ref _yawVelocity, 0.08f, Mathf.Infinity, dt);

            // --- 잡혀 있을 때 흔들림 (잡은 지점 기준 진자) ---
            if (Body.State == BodyState.Held)
            {
                float vx = dt > 0 ? (Body.Position.X - _lastHeldPos.X) / dt : 0f;
                _heldVx = Mathf.Lerp(_heldVx, vx, 1f - Mathf.Exp(-dt * 10f));
                float targetTilt = Mathf.Clamp(-_heldVx * 0.03f, -40f, 40f);
                _tilt = Mathf.SmoothDamp(_tilt, targetTilt, ref _tiltVelocity, 0.12f, Mathf.Infinity, dt);
            }
            else
            {
                _tilt = Mathf.SmoothDamp(_tilt, 0f, ref _tiltVelocity, 0.1f, Mathf.Infinity, dt);
                _heldVx = 0;
            }
            _lastHeldPos = Body.Position;

            var rot = Quaternion.AngleAxis(_tilt, Vector3.forward);
            Vector3 pos = foot.ToVector3(Depth);
            if (Mathf.Abs(_tilt) > 0.01f)
            {
                var grab = space.DesktopToWorld(GrabPoint).ToVector3(Depth);
                pos = grab + rot * (pos - grab);
            }
            transform.SetPositionAndRotation(pos, rot);
            Visual.localRotation = Quaternion.Euler(0, _yaw, 0);
        }

        /// <summary>화면 픽셀 기준 대략적인 캐릭터 사각형 (알파 히트테스트 대상 선택/디버그).</summary>
        public RectPx ScreenRect
        {
            get
            {
                var p = Body.Position;
                float w = Body.Height * 0.6f;
                return new RectPx(p.X - w * 0.5f, p.Y - Body.Height, p.X + w * 0.5f, p.Y);
            }
        }

        public void DestroyCharacter()
        {
            if (_destroyed) return;
            _destroyed = true;
            try
            {
                var avatar = Rig?.CreatedAvatar;
                _loaded?.Resources?.Dispose();
                if (avatar != null) UnityGlue.SafeDestroy(avatar);
            }
            catch (Exception e)
            {
                Log.Warn($"[{Id}] resource dispose: {e.Message}");
            }
            UnityGlue.SafeDestroy(gameObject);
        }
    }
}
