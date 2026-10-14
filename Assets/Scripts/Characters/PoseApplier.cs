using System.Collections.Generic;
using Shimeji.Core;
using UnityEngine;

namespace Shimeji.Characters
{
    /// <summary>
    /// PoseParams를 본 Transform에 적용한다. 기준 공간(space)은 캐릭터 정면이 +Z가 되도록 정렬된 Transform.
    /// 휴식 포즈(로드 직후 포즈)를 기준으로 "자식 본 방향"을 목표 방향으로 돌리는 방식이라
    /// T/A 포즈나 본 축 규칙과 무관하게 동작한다.
    /// </summary>
    /// <summary>의미 단위 포즈(PoseParams)를 본에 적용하는 방식 (3D 리그 / 2D 이미지 리그).</summary>
    public interface IPoseApplier
    {
        void Apply(PoseParams p);
    }

    public sealed class PoseApplier : IPoseApplier
    {
        readonly Transform _space;
        readonly Dictionary<BoneKey, Transform> _bones;
        readonly Dictionary<BoneKey, Quaternion> _restRot = new Dictionary<BoneKey, Quaternion>();
        readonly Dictionary<BoneKey, Vector3> _restDir = new Dictionary<BoneKey, Vector3>();
        Vector3 _hipsRestPos;
        readonly float _hipHeight;

        public PoseApplier(Transform space, Dictionary<BoneKey, Transform> bones)
        {
            _space = space;
            _bones = bones;
            var inv = Quaternion.Inverse(space.rotation);
            foreach (var kv in bones)
            {
                if (kv.Value == null) continue;
                _restRot[kv.Key] = inv * kv.Value.rotation;
            }
            AddDir(BoneKey.LeftUpperArm, BoneKey.LeftLowerArm);
            AddDir(BoneKey.LeftLowerArm, BoneKey.LeftHand);
            AddDir(BoneKey.RightUpperArm, BoneKey.RightLowerArm);
            AddDir(BoneKey.RightLowerArm, BoneKey.RightHand);
            AddDir(BoneKey.LeftUpperLeg, BoneKey.LeftLowerLeg);
            AddDir(BoneKey.LeftLowerLeg, BoneKey.LeftFoot);
            AddDir(BoneKey.RightUpperLeg, BoneKey.RightLowerLeg);
            AddDir(BoneKey.RightLowerLeg, BoneKey.RightFoot);
            // 팔/다리 한 마디짜리 제네릭(UpperArm → Hand, UpperLeg → Foot)도 지원
            if (!_restDir.ContainsKey(BoneKey.LeftUpperArm)) AddDir(BoneKey.LeftUpperArm, BoneKey.LeftHand);
            if (!_restDir.ContainsKey(BoneKey.RightUpperArm)) AddDir(BoneKey.RightUpperArm, BoneKey.RightHand);
            if (!_restDir.ContainsKey(BoneKey.LeftUpperLeg)) AddDir(BoneKey.LeftUpperLeg, BoneKey.LeftFoot);
            if (!_restDir.ContainsKey(BoneKey.RightUpperLeg)) AddDir(BoneKey.RightUpperLeg, BoneKey.RightFoot);

            if (bones.TryGetValue(BoneKey.Hips, out var hips) && hips != null)
            {
                _hipsRestPos = space.InverseTransformPoint(hips.position);
                float footY = _hipsRestPos.y;
                foreach (var k in new[] { BoneKey.LeftFoot, BoneKey.RightFoot })
                    if (bones.TryGetValue(k, out var f) && f != null) footY = Mathf.Min(footY, space.InverseTransformPoint(f.position).y);
                _hipHeight = Mathf.Max(0.001f, _hipsRestPos.y - footY);
            }
        }

        void AddDir(BoneKey from, BoneKey to)
        {
            if (!_bones.TryGetValue(from, out var a) || a == null || !_bones.TryGetValue(to, out var b) || b == null) return;
            var d = _space.InverseTransformDirection(b.position - a.position);
            if (d.sqrMagnitude < 1e-10f) return;
            _restDir[from] = d.normalized;
        }

        public bool Has(BoneKey k) => _bones.TryGetValue(k, out var t) && t != null;

        static Quaternion Euler(float pitch, float yaw, float roll) =>
            Quaternion.AngleAxis(yaw, Vector3.up) * Quaternion.AngleAxis(pitch, Vector3.right) * Quaternion.AngleAxis(roll, Vector3.forward);

        void SetModelRot(BoneKey k, Quaternion modelRot)
        {
            if (_bones.TryGetValue(k, out var t) && t != null) t.rotation = _space.rotation * modelRot;
        }

        Quaternion Rest(BoneKey k) => _restRot.TryGetValue(k, out var q) ? q : Quaternion.identity;

        /// <summary>포즈 적용. weight로 휴식 포즈와 블렌드(0 = 휴식 포즈 그대로).</summary>
        public void Apply(PoseParams p)
        {
            // --- 몸통 ---
            var rHips = Euler(p.HipsPitch, p.HipsYaw, p.HipsRoll);
            if (Has(BoneKey.Hips))
            {
                SetModelRot(BoneKey.Hips, rHips * Rest(BoneKey.Hips));
                var pos = _hipsRestPos + Vector3.down * (p.HipsDrop * _hipHeight);
                _bones[BoneKey.Hips].position = _space.TransformPoint(pos);
            }
            var rTorso = rHips * Euler(p.SpinePitch, 0, p.SpineRoll);
            SetModelRot(BoneKey.Spine, rTorso * Rest(BoneKey.Spine));
            rTorso = rTorso * Euler(p.ChestPitch, 0, 0);
            SetModelRot(BoneKey.Chest, rTorso * Rest(BoneKey.Chest));
            SetModelRot(BoneKey.UpperChest, rTorso * Rest(BoneKey.UpperChest));
            SetModelRot(BoneKey.LeftShoulder, rTorso * Rest(BoneKey.LeftShoulder));
            SetModelRot(BoneKey.RightShoulder, rTorso * Rest(BoneKey.RightShoulder));
            SetModelRot(BoneKey.Neck, rTorso * Rest(BoneKey.Neck));
            SetModelRot(BoneKey.Head, rTorso * Euler(p.HeadPitch, p.HeadYaw, p.HeadRoll) * Rest(BoneKey.Head));

            // --- 팔 ---
            ApplyArm(p.ArmL, -1, rTorso, BoneKey.LeftUpperArm, BoneKey.LeftLowerArm, BoneKey.LeftHand);
            ApplyArm(p.ArmR, 1, rTorso, BoneKey.RightUpperArm, BoneKey.RightLowerArm, BoneKey.RightHand);

            // --- 다리 ---
            ApplyLeg(p.LegL, -1, rHips, BoneKey.LeftUpperLeg, BoneKey.LeftLowerLeg, BoneKey.LeftFoot, BoneKey.LeftToes);
            ApplyLeg(p.LegR, 1, rHips, BoneKey.RightUpperLeg, BoneKey.RightLowerLeg, BoneKey.RightFoot, BoneKey.RightToes);
        }

        static Vector3 Toward(Vector3 from, Vector3 target, float degrees)
        {
            if (degrees < 0) { target = -target; degrees = -degrees; }
            return Vector3.RotateTowards(from, target, degrees * Mathf.Deg2Rad, 0f);
        }

        void ApplyArm(PoseParams.Limb limb, int side, Quaternion rTorso, BoneKey upper, BoneKey lower, BoneKey hand)
        {
            if (!_restDir.TryGetValue(upper, out var restUpper)) return;
            float a = limb.Spread * Mathf.Deg2Rad;
            var d = new Vector3(side * Mathf.Sin(a), -Mathf.Cos(a), 0f);
            d = Toward(d, Vector3.forward, limb.Forward);
            var dUpper = rTorso * d;
            var qUpper = Quaternion.FromToRotation(restUpper, dUpper);
            SetModelRot(upper, qUpper * Rest(upper));

            if (_restDir.TryGetValue(lower, out var restLower))
            {
                var bendTarget = rTorso * (Vector3.forward + Vector3.up * 0.3f).normalized;
                var dLower = Toward(dUpper, bendTarget, limb.Bend);
                var qLower = Quaternion.FromToRotation(restLower, dLower);
                SetModelRot(lower, qLower * Rest(lower));
                SetModelRot(hand, qLower * Rest(hand));
            }
            else
            {
                SetModelRot(hand, qUpper * Rest(hand));
            }
        }

        void ApplyLeg(PoseParams.Limb limb, int side, Quaternion rHips, BoneKey upper, BoneKey lower, BoneKey foot, BoneKey toes)
        {
            if (!_restDir.TryGetValue(upper, out var restUpper)) return;
            var d = Toward(Vector3.down, new Vector3(side, 0, 0), limb.Spread);
            d = Toward(d, Vector3.forward, limb.Forward);
            var dUpper = rHips * d;
            var qUpper = Quaternion.FromToRotation(restUpper, dUpper);
            SetModelRot(upper, qUpper * Rest(upper));

            var qFoot = qUpper;
            if (_restDir.TryGetValue(lower, out var restLower) && Has(lower))
            {
                var dLower = Quaternion.AngleAxis(limb.Bend, rHips * Vector3.right) * dUpper;
                var qLower = Quaternion.FromToRotation(restLower, dLower);
                SetModelRot(lower, qLower * Rest(lower));
                qFoot = qLower;
            }
            // 발은 정강이를 따라가되 서 있을 땐 바닥과 평행을 유지
            SetModelRot(foot, Quaternion.Slerp(qFoot, rHips, 0.6f) * Rest(foot));
            SetModelRot(toes, Quaternion.Slerp(qFoot, rHips, 0.6f) * Rest(toes));
        }
    }
}
