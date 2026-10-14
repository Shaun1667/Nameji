using System;
using System.Collections.Generic;
using System.Linq;
using Shimeji.Core;
using UnityEngine;

namespace Shimeji.Characters
{
    public enum RigKind { Humanoid, Generic, Sprite2D }

    public sealed class RigException : Exception
    {
        public RigException(string msg) : base(msg) { }
    }

    /// <summary>리그 판별 결과: 휴머노이드(Animator + Avatar) 또는 제네릭(본 매핑).</summary>
    public sealed class CharacterRig
    {
        public RigKind Kind;
        public Animator Animator;
        public readonly Dictionary<BoneKey, Transform> Bones = new Dictionary<BoneKey, Transform>();
        public BoneMapResult Mapping;
        public readonly List<string> Warnings = new List<string>();
        /// <summary>Unity가 만든(또는 우리가 만든) 아바타. 제거 시 파괴.</summary>
        public Avatar CreatedAvatar;

        public Transform Get(BoneKey k) => Bones.TryGetValue(k, out var t) ? t : null;
    }

    /// <summary>
    /// 1) 기존 Animator.avatar.isHuman 이면 Humanoid
    /// 2) 아니면 본 이름 휴리스틱 → 휴머노이드 아바타를 런타임 생성 시도
    /// 3) 실패하면 Generic (필수 본 Hips/Head/양발 필요)
    /// </summary>
    public static class CharacterRigBuilder
    {
        public static CharacterRig Build(GameObject modelRoot, CharacterConfig cfg)
        {
            var rig = new CharacterRig();
            var nodes = new List<BoneNode>();
            var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            Collect(modelRoot.transform, 0, nodes, byName, isRoot: true);

            rig.Mapping = BoneMapper.Map(nodes, cfg.BoneMapOverride);
            rig.Warnings.AddRange(rig.Mapping.Warnings);
            foreach (var kv in rig.Mapping.Map)
                if (byName.TryGetValue(kv.Value, out var t)) rig.Bones[kv.Key] = t;

            var animator = modelRoot.GetComponent<Animator>();
            bool existingHuman = animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman;

            if (cfg.RigType != RigType.Generic && existingHuman)
            {
                rig.Kind = RigKind.Humanoid;
                rig.Animator = animator;
                // 아바타가 알고 있는 본으로 매핑을 덮어쓴다(가장 정확).
                foreach (BoneKey k in Enum.GetValues(typeof(BoneKey)))
                {
                    var hbb = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), k.ToString());
                    var t = animator.GetBoneTransform(hbb);
                    if (t != null) rig.Bones[k] = t;
                }
                return rig;
            }

            if (cfg.RigType != RigType.Generic && rig.Mapping.CanBuildHumanoid)
            {
                var avatar = TryBuildHumanAvatar(modelRoot, rig, out var error);
                if (avatar != null)
                {
                    if (animator == null) animator = modelRoot.AddComponent<Animator>();
                    animator.avatar = avatar;
                    animator.Rebind();
                    rig.Kind = RigKind.Humanoid;
                    rig.Animator = animator;
                    rig.CreatedAvatar = avatar;
                    return rig;
                }
                rig.Warnings.Add("humanoid avatar build failed: " + error);
            }

            if (cfg.RigType == RigType.Humanoid)
                throw new RigException("rigType is 'humanoid' but humanoid avatar could not be built. Missing: " +
                                       string.Join(", ", BoneMapper.HumanoidRequired.Where(k => !rig.Mapping.Map.ContainsKey(k))));

            if (!rig.Mapping.IsValid)
                throw new RigException("required bones not found: " + string.Join(", ", rig.Mapping.MissingRequired) +
                                       ". Use boneMapOverride in character.json.");

            rig.Kind = RigKind.Generic;
            rig.Animator = animator; // 있으면 끈다(절차 애니메이션과 충돌 방지)
            if (animator != null) animator.enabled = false;
            return rig;
        }

        static void Collect(Transform t, int depth, List<BoneNode> nodes, Dictionary<string, Transform> byName, bool isRoot)
        {
            if (!isRoot)
            {
                nodes.Add(new BoneNode(t.name, depth));
                if (!byName.ContainsKey(t.name)) byName[t.name] = t;
            }
            foreach (Transform c in t) Collect(c, depth + 1, nodes, byName, false);
        }

        static Avatar TryBuildHumanAvatar(GameObject root, CharacterRig rig, out string error)
        {
            error = null;
            try
            {
                var human = new List<HumanBone>();
                foreach (var kv in rig.Bones)
                {
                    var hbb = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), kv.Key.ToString());
                    human.Add(new HumanBone
                    {
                        boneName = kv.Value.name,
                        humanName = HumanTrait.BoneName[(int)hbb],
                        limit = new HumanLimit { useDefaultValues = true },
                    });
                }
                var skeleton = new List<SkeletonBone>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    skeleton.Add(new SkeletonBone
                    {
                        name = t.name,
                        position = t.localPosition,
                        rotation = t.localRotation,
                        scale = t.localScale,
                    });
                }
                var desc = new HumanDescription
                {
                    human = human.ToArray(),
                    skeleton = skeleton.ToArray(),
                    upperArmTwist = 0.5f,
                    lowerArmTwist = 0.5f,
                    upperLegTwist = 0.5f,
                    lowerLegTwist = 0.5f,
                    armStretch = 0.05f,
                    legStretch = 0.05f,
                    feetSpacing = 0f,
                    hasTranslationDoF = false,
                };
                var avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
                if (avatar == null || !avatar.isValid || !avatar.isHuman)
                {
                    error = "AvatarBuilder returned invalid avatar";
                    if (avatar != null) UnityGlue.SafeDestroy(avatar);
                    return null;
                }
                avatar.name = root.name + "_RuntimeAvatar";
                return avatar;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }
    }
}
