using System.Collections.Generic;
using System.Linq;
using Shimeji.Core;
using UnityEngine;

namespace Shimeji.Characters
{
    /// <summary>애니메이션 출처 (테스트/디버그용).</summary>
    public enum AnimationSource { None, ModelClip, Library, Procedural }

    /// <summary>
    /// 3단 애니메이션:
    ///  1) 모델 내장/animations 폴더 클립 (이름 규칙 일치 시 최우선, legacy Animation)
    ///  2) 휴머노이드 공용 클립 라이브러리 (AnimatorOverrideController)
    ///  3) 제네릭 리그 폴백: 본 매핑 + 사인파 절차 애니메이션
    /// 그 위에 절차적 보정(팔 벌림 보정, 시선 추적)을 LateUpdate에서 얹는다.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class CharacterAnimator : MonoBehaviour
    {
        public const float CrossFade = 0.2f;

        CharacterRig _rig;
        Animator _animator;
        Animation _legacy;
        AnimatorOverrideController _override;
        readonly Dictionary<string, string> _modelClipByKey = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _libraryKeys = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        PoseApplier _poser;
        PoseParams _poseFrom, _poseCurrent;
        float _poseBlend = 1f;
        float _poseTime;
        float _speed = 1f;
        float _armSpreadBias;

        Transform _head;
        Transform _space;
        Quaternion _lookOffset = Quaternion.identity;

        public string CurrentKey { get; private set; }
        public AnimationSource CurrentSource { get; private set; }
        public RigKind RigKind => _rig?.Kind ?? RigKind.Generic;
        public IReadOnlyDictionary<string, string> ModelClipMap => _modelClipByKey;

        /// <summary>시선 목표(월드). null이면 정면.</summary>
        public Vector3? LookTarget { get; set; }
        public bool LookAtEnabled { get; set; } = true;
        /// <summary>시선 추적 최대 각도.</summary>
        public float LookMaxAngle = 35f;

        public void Setup(CharacterRig rig, Transform space, RuntimeAnimatorController baseController,
            IReadOnlyList<AnimationClip> libraryClips, CharacterConfig cfg, IEnumerable<AnimationClip> extraClips)
        {
            _rig = rig;
            _space = space;
            _armSpreadBias = cfg.ArmSpreadBias;
            _head = rig.Get(BoneKey.Head);

            // --- 1) 모델 클립 ---
            var root = rig.Animator != null ? rig.Animator.gameObject : FindLegacyRoot(space);
            _legacy = root != null ? root.GetComponent<Animation>() : null;
            if (extraClips != null)
            {
                foreach (var c in extraClips)
                {
                    if (c == null) continue;
                    if (_legacy == null && root != null) _legacy = root.AddComponent<Animation>();
                    _legacy?.AddClip(c, c.name);
                }
            }
            if (_legacy != null)
            {
                _legacy.playAutomatically = false;
                _legacy.Stop();
                _legacy.cullingType = AnimationCullingType.AlwaysAnimate;
                var names = new List<string>();
                foreach (AnimationState s in _legacy) names.Add(s.name);
                foreach (var kv in ClipNameMatcher.Match(names, AnimKeys.All)) _modelClipByKey[kv.Key] = kv.Value;
                // clipOverrides: 키 → 모델 클립 이름 직접 지정
                foreach (var kv in cfg.ClipOverrides)
                    if (names.Contains(kv.Value)) _modelClipByKey[kv.Key] = kv.Value;
            }

            // --- 2) 휴머노이드 라이브러리 ---
            if (rig.Kind == RigKind.Humanoid && baseController != null)
            {
                _animator = rig.Animator;
                _override = new AnimatorOverrideController(baseController) { name = "Override_" + cfg.DisplayName };
                var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                _override.GetOverrides(overrides);
                for (int i = 0; i < overrides.Count; i++)
                {
                    var original = overrides[i].Key;
                    _libraryKeys.Add(original.name);
                    // clipOverrides가 다른 라이브러리 클립을 가리키면 교체
                    if (cfg.ClipOverrides.TryGetValue(original.name, out var replacementName))
                    {
                        var rep = libraryClips?.FirstOrDefault(c => c != null && c.name == replacementName);
                        if (rep != null) overrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(original, rep);
                    }
                }
                _override.ApplyOverrides(overrides);
                _animator.runtimeAnimatorController = _override;
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.enabled = true;
            }
            else
            {
                // --- 3) 제네릭 폴백 ---
                if (rig.Animator != null) rig.Animator.enabled = false;
                _poser = new PoseApplier(space, rig.Bones);
            }
        }

        static GameObject FindLegacyRoot(Transform space)
        {
            var anim = space.GetComponentInChildren<Animation>(true);
            if (anim != null) return anim.gameObject;
            return space.childCount > 0 ? space.GetChild(0).gameObject : null;
        }

        public bool HasModelClip(string key) => _modelClipByKey.ContainsKey(key);

        public AnimationSource ResolveSource(string key)
        {
            if (_modelClipByKey.ContainsKey(key)) return AnimationSource.ModelClip;
            if (_animator != null && _libraryKeys.Contains(key)) return AnimationSource.Library;
            return AnimationSource.Procedural;
        }

        /// <summary>상태 키 재생. 같은 키면 속도만 갱신.</summary>
        public void Play(string key, float speed = 1f)
        {
            _speed = speed;
            if (key == CurrentKey)
            {
                ApplySpeed();
                return;
            }
            var source = ResolveSource(key);
            if (source == AnimationSource.Procedural && _poser == null && _animator != null)
            {
                // 휴머노이드인데 라이브러리에 없는 키 → 대체 키
                key = AnimKeys.Fallback(key);
                source = ResolveSource(key);
            }

            switch (source)
            {
                case AnimationSource.ModelClip:
                {
                    if (_animator != null) _animator.enabled = false;
                    var name = _modelClipByKey[key];
                    var spec = ProceduralPoses.GetSpec(key);
                    _legacy[name].wrapMode = spec != null && !spec.Loop ? WrapMode.ClampForever : WrapMode.Loop;
                    if (CurrentSource == AnimationSource.ModelClip) _legacy.CrossFade(name, CrossFade);
                    else _legacy.Play(name);
                    break;
                }
                case AnimationSource.Library:
                    if (_legacy != null && _legacy.isPlaying) _legacy.Stop();
                    if (!_animator.enabled) _animator.enabled = true;
                    _animator.CrossFadeInFixedTime(key, CurrentSource == AnimationSource.Library ? CrossFade : 0f);
                    break;
                default:
                    if (_legacy != null && _legacy.isPlaying) _legacy.Stop();
                    _poseFrom = _poseCurrent;
                    _poseBlend = CurrentSource == AnimationSource.Procedural ? 0f : 1f;
                    _poseTime = 0f;
                    break;
            }
            CurrentKey = key;
            CurrentSource = source;
            ApplySpeed();
        }

        void ApplySpeed()
        {
            if (CurrentSource == AnimationSource.Library && _animator != null) _animator.speed = _speed;
            else if (_animator != null) _animator.speed = 1f;
            if (CurrentSource == AnimationSource.ModelClip && _legacy != null && _modelClipByKey.TryGetValue(CurrentKey, out var n))
                _legacy[n].speed = _speed;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (CurrentSource == AnimationSource.Procedural && _poser != null && CurrentKey != null)
            {
                _poseTime += dt * _speed;
                var target = ProceduralPoses.Evaluate(CurrentKey, _poseTime);
                _poseBlend = Mathf.Min(1f, _poseBlend + dt / CrossFade);
                _poseCurrent = _poseBlend >= 1f ? target : PoseParams.Lerp(_poseFrom, target, _poseBlend);
                _poser.Apply(_poseCurrent);
            }

            if (CurrentSource == AnimationSource.Library) ApplyArmSpread();
            ApplyLookAt(dt);
        }

        /// <summary>SD 비율 보정: 휴머노이드 클립 적용 시 짧은 팔이 몸통을 뚫지 않도록 바깥으로 벌린다.</summary>
        void ApplyArmSpread()
        {
            if (Mathf.Approximately(_armSpreadBias, 0f) || _space == null) return;
            var l = _rig.Get(BoneKey.LeftUpperArm);
            var r = _rig.Get(BoneKey.RightUpperArm);
            var axis = _space.forward;
            // 오른팔(+X)은 +Z축 회전이 위로, 왼팔(-X)은 -Z축 회전이 위로 벌어진다.
            if (l != null) l.rotation = Quaternion.AngleAxis(-_armSpreadBias, axis) * l.rotation;
            if (r != null) r.rotation = Quaternion.AngleAxis(_armSpreadBias, axis) * r.rotation;
        }

        void ApplyLookAt(float dt)
        {
            if (_head == null || _space == null) return;
            // 애니메이션이 머리를 쓰지 않는 프레임(legacy 클립 등)에는 지난번 보정이 누적되지 않도록 원래 값으로 되돌린다.
            if (_hasLastHead && _head.localRotation == _lastHeadWritten) _head.localRotation = _lastHeadBase;
            _lastHeadBase = _head.localRotation;
            var desired = Quaternion.identity;
            if (LookAtEnabled && LookTarget.HasValue)
            {
                var forward = _space.forward;
                var dir = LookTarget.Value - _head.position;
                if (dir.sqrMagnitude > 1e-6f)
                {
                    desired = Quaternion.FromToRotation(forward, dir.normalized);
                    float angle = Quaternion.Angle(Quaternion.identity, desired);
                    if (angle > LookMaxAngle) desired = Quaternion.Slerp(Quaternion.identity, desired, LookMaxAngle / angle);
                }
            }
            _lookOffset = Quaternion.Slerp(_lookOffset, desired, 1f - Mathf.Exp(-dt * 6f));
            _head.rotation = _lookOffset * _head.rotation;
            _lastHeadWritten = _head.localRotation;
            _hasLastHead = true;
        }

        bool _hasLastHead;
        Quaternion _lastHeadWritten, _lastHeadBase;
    }
}
