using System;
using System.Collections.Generic;
using System.IO;
using Shimeji.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Shimeji.Characters
{
    /// <summary>2D 이미지 캐릭터 정보 (관절 편집 UI가 사용).</summary>
    public sealed class SpriteInfo
    {
        public string ImagePath;
        public int ImageWidth, ImageHeight;
        public SpriteRigResult Rig;
        /// <summary>이미지 픽셀 → 모델 로컬(Oriented 기준) 변환 정보.</summary>
        public float PixelScale;   // 1 / 캐릭터 높이(px)
        public float OriginX, OriginY; // 픽셀 기준 원점 (발 중앙)

        public Vector3 PixelToModel(Vec2 px) => new Vector3((px.X - OriginX) * PixelScale, (OriginY - px.Y) * PixelScale, 0f);
        public Vec2 ModelToPixel(Vector3 m) => new Vec2(m.x / PixelScale + OriginX, OriginY - m.y / PixelScale);
    }

    /// <summary>
    /// PNG/JPG 한 장으로 2D 스켈레톤 캐릭터를 만든다:
    /// 알파 마스크 → 관절 자동 감지(또는 character.json joints) → 격자 메시 → 자동 가중치 → SkinnedMesh.
    /// </summary>
    public static class SpriteModelBuilder
    {
        public const string ShaderName = "Shimeji/Sprite";
        public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg" };

        sealed class Disposer : IDisposable
        {
            readonly List<UnityEngine.Object> _objs;
            public Disposer(List<UnityEngine.Object> objs) { _objs = objs; }
            public void Dispose() { foreach (var o in _objs) UnityGlue.SafeDestroy(o); }
        }

        public static bool IsImage(string path) =>
            Array.IndexOf(ImageExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        public static AlphaMask MaskFromTexture(Texture2D tex)
        {
            var px = tex.GetPixels32();
            int w = tex.width, h = tex.height;
            var a = new byte[w * h];
            // Unity 텍스처는 아래 행부터 → 마스크는 위 행부터
            for (int y = 0; y < h; y++)
            {
                int src = (h - 1 - y) * w, dst = y * w;
                for (int x = 0; x < w; x++) a[dst + x] = px[src + x].a;
            }
            return new AlphaMask(w, h, a);
        }

        public static LoadedModel Build(string imagePath, CharacterConfig cfg)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = Path.GetFileName(imagePath) };
            if (!tex.LoadImage(File.ReadAllBytes(imagePath), false))
            {
                UnityGlue.SafeDestroy(tex);
                throw new InvalidDataException("cannot decode image: " + imagePath);
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            try
            {
                return Build(tex, cfg, imagePath);
            }
            catch
            {
                UnityGlue.SafeDestroy(tex);
                throw;
            }
        }

        public static LoadedModel Build(Texture2D tex, CharacterConfig cfg, string imagePath)
        {
            var mask = MaskFromTexture(tex);
            bool hasAlpha = false;
            foreach (var b in mask.Alpha) if (b < 250) { hasAlpha = true; break; }
            if (!hasAlpha) throw new InvalidDataException("image has no transparency (use a PNG with transparent background)");

            var rig = SpriteJointDetector.Detect(mask);
            if (cfg.Joints.Count > 0) SpriteJointDetector.ApplyOverrides(rig, cfg.Joints, mask.Width, mask.Height);
            var bones = SpriteSkinning.BuildBones(rig);
            int H = (int)rig.Bounds.Height;
            int cell = Mathf.Clamp(H / 48, 3, 24);
            var sm = SpriteSkinning.BuildMesh(mask, rig.Bounds, cell);
            SpriteSkinning.ComputeWeights(sm, bones, rig);
            Log.Info($"[sprite] {Path.GetFileName(imagePath)} {mask.Width}x{mask.Height}: confidence={rig.Confidence:0.00} " +
                     $"neck={rig.NeckFound} legs={rig.LegsSeparated} armA={rig.ArmAFound} armB={rig.ArmBFound} " +
                     $"verts={sm.Vertices.Count} {string.Join("; ", rig.Notes)}");

            var info = new SpriteInfo
            {
                ImagePath = imagePath,
                ImageWidth = mask.Width,
                ImageHeight = mask.Height,
                Rig = rig,
                PixelScale = 1f / Mathf.Max(1, H),
                OriginX = rig.Bounds.Center.X,
                OriginY = rig.Bounds.Bottom,
            };

            var root = new GameObject("SpriteModel:" + Path.GetFileNameWithoutExtension(imagePath));
            // 본 계층 (휴식 회전 = 단위, XY 평면)
            var tfs = new Transform[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                var t = new GameObject(bones[i].Name).transform;
                t.SetParent(bones[i].Parent < 0 ? root.transform : tfs[bones[i].Parent], false);
                t.position = info.PixelToModel(bones[i].Head);
                tfs[i] = t;
            }

            var mesh = new Mesh { name = root.name + "_Mesh" };
            mesh.indexFormat = sm.Vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            var verts = new Vector3[sm.Vertices.Count];
            var uvs = new Vector2[sm.Vertices.Count];
            var normals = new Vector3[sm.Vertices.Count];
            var weights = new BoneWeight[sm.Vertices.Count];
            for (int i = 0; i < verts.Length; i++)
            {
                var p = sm.Vertices[i];
                verts[i] = info.PixelToModel(p);
                uvs[i] = new Vector2(p.X / mask.Width, 1f - p.Y / mask.Height);
                normals[i] = Vector3.back;
                var w = sm.Weights[i];
                var bw = new BoneWeight { boneIndex0 = w[0].bone, weight0 = w[0].w };
                if (w.Length > 1) { bw.boneIndex1 = w[1].bone; bw.weight1 = w[1].w; }
                weights[i] = bw;
            }
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = sm.Triangles.ToArray();
            mesh.boneWeights = weights;
            var bind = new Matrix4x4[tfs.Length];
            for (int i = 0; i < tfs.Length; i++) bind[i] = tfs[i].worldToLocalMatrix * root.transform.localToWorldMatrix;
            mesh.bindposes = bind;
            mesh.RecalculateBounds();

            var shader = Shader.Find(ShaderName);
            if (shader == null) throw new InvalidOperationException("shader not found: " + ShaderName);
            var mat = new Material(shader) { name = root.name + "_Mat", mainTexture = tex };

            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            var smr = body.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = tfs;
            smr.rootBone = tfs[0];
            smr.sharedMaterial = mat;
            smr.updateWhenOffscreen = true;
            smr.quality = SkinQuality.Bone2;

            return new LoadedModel
            {
                Root = root,
                IsVrm = false,
                Sprite = info,
                Resources = new Disposer(new List<UnityEngine.Object> { tex, mesh, mat }),
            };
        }
    }

    /// <summary>
    /// PoseParams(3D 의미 단위 포즈)를 정면 2D 리그로 옮긴다.
    ///  - 팔 벌림/흔들기/팔꿈치 → 화면 평면 회전 (기본 대기 포즈 대비 차이만 적용해서 원래 그림 자세를 존중)
    ///  - 다리 앞으로 들기/무릎 → 원근 축소(세로 스케일) + 약간의 회전, 발이 바닥에 붙도록 엉덩이를 내린다
    ///  - 몸통/머리 기울기 → 회전, 고개 숙임/돌림 → 약간의 이동
    /// </summary>
    public sealed class SpritePoseApplier : IPoseApplier
    {
        readonly CharacterRig _rig;
        readonly Transform _root;
        readonly float _amount;
        readonly Dictionary<Transform, Vector3> _restPos = new Dictionary<Transform, Vector3>();
        readonly float _thighA, _shinA, _thighB, _shinB, _headLen;

        // 기본 대기 포즈(ProceduralPoses.Stand)의 값 — 그림의 원래 자세가 이 값에 해당한다고 본다
        const float BaseArmSpread = 20f, BaseElbow = 10f;

        public SpritePoseApplier(CharacterRig rig, Transform modelRoot, float amount)
        {
            _rig = rig;
            _root = modelRoot;
            _amount = amount;
            foreach (var t in rig.Bones.Values) if (t != null) _restPos[t] = t.localPosition;
            _thighA = Len(BoneKey.RightUpperLeg, BoneKey.RightLowerLeg);
            _shinA = Len(BoneKey.RightLowerLeg, BoneKey.RightFoot);
            _thighB = Len(BoneKey.LeftUpperLeg, BoneKey.LeftLowerLeg);
            _shinB = Len(BoneKey.LeftLowerLeg, BoneKey.LeftFoot);
            var head = rig.Get(BoneKey.Head);
            // 모델 로컬 단위(키 = 1, 발 = 0) 기준
            _headLen = head != null ? Mathf.Max(0.05f, 1f - _root.InverseTransformPoint(head.position).y) : 0.3f;
        }

        float Len(BoneKey a, BoneKey b)
        {
            var ta = _rig.Get(a);
            var tb = _rig.Get(b);
            return ta != null && tb != null ? Vector3.Distance(_root.InverseTransformPoint(ta.position), _root.InverseTransformPoint(tb.position)) : 0f;
        }

        static void Rot(Transform t, float deg)
        {
            if (t != null) t.localRotation = Quaternion.Euler(0, 0, deg);
        }

        static void ScaleY(Transform t, float s)
        {
            if (t != null) t.localScale = new Vector3(1f, s, 1f);
        }

        Vector3 Rest(Transform t) => t != null && _restPos.TryGetValue(t, out var p) ? p : Vector3.zero;

        /// <summary>휴식 포즈로 되돌린다 (관절 편집용).</summary>
        public void ApplyRest()
        {
            foreach (var kv in _restPos)
            {
                kv.Key.localPosition = kv.Value;
                kv.Key.localRotation = Quaternion.identity;
                kv.Key.localScale = Vector3.one;
            }
        }

        public void Apply(PoseParams p)
        {
            float k = _amount;

            // --- 다리: 앞으로 든 만큼 원근 축소 ---
            float tsA = Foreshorten(p.LegR.Forward * k), ssA = Foreshorten((p.LegR.Forward - p.LegR.Bend) * k);
            float tsB = Foreshorten(p.LegL.Forward * k), ssB = Foreshorten((p.LegL.Forward - p.LegL.Bend) * k);
            ApplyLeg(_rig.Get(BoneKey.RightUpperLeg), _rig.Get(BoneKey.RightLowerLeg), tsA, ssA, -1, p.LegR, k);
            ApplyLeg(_rig.Get(BoneKey.LeftUpperLeg), _rig.Get(BoneKey.LeftLowerLeg), tsB, ssB, 1, p.LegL, k);

            // 발이 바닥에 남도록 엉덩이를 내린다 (두 다리 중 긴 쪽 기준)
            float legA = _thighA * tsA + _shinA * ssA, legB = _thighB * tsB + _shinB * ssB;
            float drop = Mathf.Max(0f, Mathf.Min(_thighA + _shinA - legA, _thighB + _shinB - legB));
            var hips = _rig.Get(BoneKey.Hips);
            if (hips != null)
            {
                hips.localPosition = Rest(hips) + new Vector3(0f, -drop, 0f);
                Rot(hips, p.HipsRoll * 0.6f * k);
            }

            // --- 몸통 ---
            var spine = _rig.Get(BoneKey.Spine);
            if (spine != null)
            {
                Rot(spine, (p.SpineRoll + p.HipsYaw * 0.25f) * k);
                ScaleY(spine, 1f - Mathf.Clamp01(Mathf.Abs(p.SpinePitch + p.ChestPitch) / 90f) * 0.25f * k);
            }

            // --- 머리 ---
            var head = _rig.Get(BoneKey.Head);
            if (head != null)
            {
                Rot(head, p.HeadRoll * k);
                head.localPosition = Rest(head) + new Vector3(p.HeadYaw / 90f * 0.12f * _headLen, -p.HeadPitch / 90f * 0.15f * _headLen, 0f) * k;
            }

            // --- 팔 (오른팔 = 이미지 왼쪽, 벌리면 시계 방향) ---
            ApplyArm(_rig.Get(BoneKey.RightUpperArm), _rig.Get(BoneKey.RightLowerArm), p.ArmR, -1, k);
            ApplyArm(_rig.Get(BoneKey.LeftUpperArm), _rig.Get(BoneKey.LeftLowerArm), p.ArmL, 1, k);
        }

        static float Foreshorten(float forwardDeg) =>
            Mathf.Max(0.42f, Mathf.Cos(Mathf.Clamp(Mathf.Abs(forwardDeg), 0f, 90f) * Mathf.Deg2Rad));

        static void ApplyArm(Transform upper, Transform lower, PoseParams.Limb limb, int side, float k)
        {
            float spread = (limb.Spread - BaseArmSpread) * k;
            float swing = limb.Forward * 0.3f * k;
            Rot(upper, side * (spread + swing));
            Rot(lower, side * (limb.Bend - BaseElbow) * 0.5f * k);
        }

        void ApplyLeg(Transform thigh, Transform shin, float thighScale, float shinScale, int side, PoseParams.Limb limb, float k)
        {
            if (thigh == null) return;
            thigh.localScale = new Vector3(1f, thighScale, 1f);
            Rot(thigh, side * limb.Spread * 0.6f * k + limb.Forward * 0.12f * k);
            if (shin != null)
            {
                // 부모의 세로 스케일을 상쇄해서 정강이 길이는 shinScale만 반영
                shin.localScale = new Vector3(1f, shinScale / Mathf.Max(0.01f, thighScale), 1f);
                Rot(shin, -limb.Forward * 0.08f * k);
            }
        }
    }
}
