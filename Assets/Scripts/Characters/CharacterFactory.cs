using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shimeji.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Shimeji.Characters
{
    /// <summary>캐릭터 생성에 필요한 공용 에셋 (씬에서 주입).</summary>
    public sealed class CharacterAssets
    {
        public RuntimeAnimatorController BaseController;
        public IReadOnlyList<AnimationClip> LibraryClips = Array.Empty<AnimationClip>();
        public float PixelsPerUnit = ScreenSpace.DefaultPixelsPerUnit;
    }

    /// <summary>
    /// 로드된 모델을 시메지 캐릭터로 조립한다.
    ///  Character(root, CharacterActor) → Visual(방향/기울기/사용자 크기) → Oriented(정면 보정, 키 정규화, 발 피벗) → Model
    /// </summary>
    public static class CharacterFactory
    {
        /// <summary>폴더 하나를 로드해 캐릭터를 만든다. 실패 시 예외 (호출자가 로그를 남기고 계속 진행).</summary>
        public static async Task<CharacterActor> CreateFromFolderAsync(string folder, CharacterAssets assets)
        {
            var name = Path.GetFileName(folder.TrimEnd('/', '\\'));
            var warnings = new List<string>();
            var cfgPath = Path.Combine(folder, "character.json");
            var cfg = File.Exists(cfgPath)
                ? CharacterConfig.Parse(File.ReadAllText(cfgPath), name, warnings)
                : CharacterConfig.Default(name);
            foreach (var w in warnings) Log.Warn($"[{name}] {w}");

            var modelPath = ModelImporterRegistry.FindModelFile(folder);
            if (modelPath == null) throw new FileNotFoundException($"no model file (model.vrm/.glb/.gltf) in '{folder}'");
            var importer = ModelImporterRegistry.Find(modelPath);
            if (importer == null) throw new NotSupportedException("unsupported model format: " + modelPath);

            var loaded = await importer.LoadAsync(modelPath);
            List<AnimationClip> extra;
            try
            {
                extra = await ModelImporterRegistry.LoadExtraClipsAsync(folder);
            }
            catch (Exception e)
            {
                Log.Warn($"[{name}] extra animations failed: {e.Message}");
                extra = new List<AnimationClip>();
            }

            try
            {
                return Assemble(name, loaded, cfg, assets, extra);
            }
            catch
            {
                loaded.Resources?.Dispose();
                if (loaded.Root != null) UnityGlue.SafeDestroy(loaded.Root);
                throw;
            }
        }

        /// <summary>에디터 FBX 임포트 경로로 만든 프리팹에서 캐릭터 생성.</summary>
        public static CharacterActor CreateFromPrefab(GameObject prefab, string configJson, CharacterAssets assets)
        {
            var warnings = new List<string>();
            var cfg = CharacterConfig.Parse(configJson, prefab.name, warnings);
            foreach (var w in warnings) Log.Warn($"[{prefab.name}] {w}");
            var instance = UnityEngine.Object.Instantiate(prefab);
            instance.name = prefab.name;
            var loaded = new LoadedModel { Root = instance, IsVrm = false };
            try
            {
                return Assemble(prefab.name, loaded, cfg, assets, null);
            }
            catch
            {
                UnityGlue.SafeDestroy(instance);
                throw;
            }
        }

        public static CharacterActor Assemble(string id, LoadedModel loaded, CharacterConfig cfg, CharacterAssets assets, IEnumerable<AnimationClip> extraClips)
        {
            var root = new GameObject("Character:" + id);
            var visual = new GameObject("Visual").transform;
            visual.SetParent(root.transform, false);
            var oriented = new GameObject("Oriented").transform;
            oriented.SetParent(visual, false);
            var model = loaded.Root.transform;
            model.SetParent(oriented, false);
            model.localPosition = Vector3.zero;
            model.localRotation = Quaternion.identity;

            try
            {
                // 1) 리그
                var rig = CharacterRigBuilder.Build(model.gameObject, cfg);
                foreach (var w in rig.Warnings) Log.Warn($"[{id}] {w}");

                // 2) 정면 방향 보정 → +Z
                oriented.localRotation = Quaternion.Euler(0, DetectYawToFaceForward(rig, oriented), 0);

                // 3) 머티리얼/렌더러
                FixRenderers(root);

                // 4) 크기 정규화 + 발바닥 피벗
                var bounds = ComputeBounds(root);
                if (bounds.size.y < 1e-5f) throw new InvalidDataException("model has no visible geometry");
                float targetHeight = cfg.HeightPixels / assets.PixelsPerUnit;
                float s = targetHeight / bounds.size.y;
                oriented.localScale = Vector3.one * s;
                oriented.localPosition = -new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) * s;

                // 5) 충돌 영역
                Physics.SyncTransforms();
                CharacterColliders.Build(root.transform, rig, cfg, targetHeight);

                // 6) 스프링본 (VRM은 자체 런타임 사용)
                if (!loaded.IsVrm && cfg.EnableSpringBones)
                {
                    var spring = root.AddComponent<SimpleSpringBone>();
                    spring.Setup(model, rig.Bones.Values);
                    if (spring.JointCount == 0) UnityGlue.SafeDestroy(spring);
                }

                // 7) 애니메이터
                var anim = root.AddComponent<CharacterAnimator>();
                anim.Setup(rig, oriented, assets.BaseController, assets.LibraryClips, cfg, extraClips);
                anim.LookAtEnabled = cfg.LookAtCursor;

                var actor = root.AddComponent<CharacterActor>();
                actor.Initialize(id, cfg, rig, anim, visual, oriented, loaded, bounds.size * s, assets.PixelsPerUnit);
                Log.Info($"[{id}] loaded: rig={rig.Kind}, bones={rig.Bones.Count}, height={cfg.HeightPixels}px, " +
                         $"modelClips={anim.ModelClipMap.Count}, vrm={loaded.IsVrm}");
                return actor;
            }
            catch
            {
                model.SetParent(null, false);
                UnityGlue.SafeDestroy(root);
                throw;
            }
        }

        /// <summary>좌우 본 위치로 모델 정면을 추정해서 +Z를 보도록 하는 Yaw(도). 판단 불가면 0.</summary>
        static float DetectYawToFaceForward(CharacterRig rig, Transform space)
        {
            Vector3? right = null;
            foreach (var (l, r) in new[]
                     {
                         (BoneKey.LeftUpperArm, BoneKey.RightUpperArm),
                         (BoneKey.LeftUpperLeg, BoneKey.RightUpperLeg),
                         (BoneKey.LeftFoot, BoneKey.RightFoot),
                     })
            {
                var lt = rig.Get(l);
                var rt = rig.Get(r);
                if (lt == null || rt == null) continue;
                var v = space.InverseTransformPoint(rt.position) - space.InverseTransformPoint(lt.position);
                v.y = 0;
                if (v.sqrMagnitude < 1e-8f) continue;
                right = v.normalized;
                break;
            }
            if (!right.HasValue) return 0f;
            var fwd = Vector3.Cross(right.Value, Vector3.up); // 왼손 좌표계: right × up = forward
            float yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            // 90도 단위로 스냅
            return -Mathf.Round(yaw / 90f) * 90f;
        }

        static void FixRenderers(GameObject root)
        {
            Shader standard = null;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;

                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    var sh = m.shader;
                    bool bad = sh == null || !sh.isSupported || sh.name.Contains("Universal Render Pipeline") || sh.name.Contains("Hidden/InternalErrorShader");
                    if (!bad) continue;
                    if (standard == null) standard = Shader.Find("Standard");
                    if (standard == null) continue;
                    var nm = new Material(standard) { name = m.name + "_Builtin" };
                    if (m.HasProperty("_MainTex")) nm.mainTexture = m.mainTexture;
                    else if (m.HasProperty("_BaseMap")) nm.mainTexture = m.GetTexture("_BaseMap");
                    if (m.HasProperty("_Color")) nm.color = m.color;
                    else if (m.HasProperty("_BaseColor")) nm.color = m.GetColor("_BaseColor");
                    mats[i] = nm;
                    changed = true;
                    Log.Warn($"material '{m.name}' shader '{sh?.name}' unsupported → Standard");
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        /// <summary>현재 포즈의 정확한 메시 바운드(월드). SkinnedMesh는 BakeMesh로 계산.</summary>
        public static Bounds ComputeBounds(GameObject root)
        {
            bool has = false;
            var b = new Bounds();
            var tmp = new Mesh();
            var verts = new List<Vector3>();
            try
            {
                foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (!r.enabled) continue;
                    Matrix4x4 m;
                    Mesh src;
                    if (r is SkinnedMeshRenderer smr)
                    {
                        if (smr.sharedMesh == null) continue;
                        // useScale=true: 렌더러 로컬 공간(스케일 역적용)으로 구워지므로 전체 localToWorld로 되돌린다.
                        // (useScale=false면 월드 크기 정점이 나와 TRS(위치, 회전)만 적용해야 함 — 테스트로 확인)
                        smr.BakeMesh(tmp, true);
                        src = tmp;
                        m = smr.transform.localToWorldMatrix;
                    }
                    else if (r is MeshRenderer)
                    {
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf == null || mf.sharedMesh == null) continue;
                        if (!mf.sharedMesh.isReadable) { Encapsulate(ref b, ref has, r.bounds); continue; }
                        src = mf.sharedMesh;
                        m = r.transform.localToWorldMatrix;
                    }
                    else continue;

                    verts.Clear();
                    src.GetVertices(verts);
                    foreach (var v in verts)
                    {
                        var p = m.MultiplyPoint3x4(v);
                        if (!has) { b = new Bounds(p, Vector3.zero); has = true; }
                        else b.Encapsulate(p);
                    }
                }
            }
            finally
            {
                UnityGlue.SafeDestroy(tmp);
            }
            return b;
        }

        static void Encapsulate(ref Bounds b, ref bool has, Bounds other)
        {
            if (!has) { b = other; has = true; }
            else b.Encapsulate(other);
        }
    }

    /// <summary>클릭 판정용 콜라이더(머리/몸통/다리). 지형 충돌에는 쓰지 않는다.</summary>
    public static class CharacterColliders
    {
        public static void Build(Transform root, CharacterRig rig, CharacterConfig cfg, float heightWorld)
        {
            float k = cfg.ColliderScale;
            var head = rig.Get(BoneKey.Head);
            var hips = rig.Get(BoneKey.Hips);
            var neck = rig.Get(BoneKey.Neck) ?? head;
            var lf = rig.Get(BoneKey.LeftFoot);
            var rf = rig.Get(BoneKey.RightFoot);
            float top = root.position.y + heightWorld;
            float ground = root.position.y;

            if (cfg.ColliderPreset == "box" || head == null || hips == null)
            {
                var go = new GameObject("Hit_Box");
                go.transform.SetParent(root.Find("Visual"), false);
                var box = go.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.center = new Vector3(0, heightWorld * 0.5f, 0);
                box.size = new Vector3(heightWorld * 0.5f * k, heightWorld * k, heightWorld * 0.5f * k);
                go.layer = UnityGlue.CharacterLayer;
                return;
            }

            // 머리: 머리 본에서 모델 꼭대기까지
            {
                float headSpan = Mathf.Max(top - head.position.y, heightWorld * 0.1f);
                float r = headSpan * 0.6f * k;
                var c = head.position + Vector3.up * headSpan * 0.45f;
                AddSphere(head, "Hit_Head", c, r);
            }
            // 몸통: 엉덩이 → 목
            {
                float r = heightWorld * 0.14f * k;
                AddCapsule(hips, "Hit_Body", hips.position, neck.position, r);
            }
            // 다리: 엉덩이 → 발
            {
                var feet = lf != null && rf != null ? (lf.position + rf.position) * 0.5f : new Vector3(hips.position.x, ground, hips.position.z);
                feet.y = Mathf.Min(feet.y, ground + heightWorld * 0.03f);
                float r = heightWorld * 0.13f * k;
                AddCapsule(hips, "Hit_Legs", hips.position, feet, r);
            }
        }

        static void AddSphere(Transform parent, string name, Vector3 worldCenter, float worldRadius)
        {
            var go = new GameObject(name) { layer = UnityGlue.CharacterLayer };
            go.transform.SetParent(parent, false);
            go.transform.position = worldCenter;
            go.transform.rotation = Quaternion.identity;
            var s = go.AddComponent<SphereCollider>();
            s.isTrigger = true;
            s.radius = worldRadius / Mathf.Max(1e-6f, go.transform.lossyScale.x);
        }

        static void AddCapsule(Transform parent, string name, Vector3 a, Vector3 b, float worldRadius)
        {
            var go = new GameObject(name) { layer = UnityGlue.CharacterLayer };
            go.transform.SetParent(parent, false);
            var dir = b - a;
            go.transform.position = (a + b) * 0.5f;
            go.transform.rotation = dir.sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(Vector3.up, dir.normalized) : Quaternion.identity;
            var c = go.AddComponent<CapsuleCollider>();
            c.isTrigger = true;
            c.direction = 1;
            float scale = Mathf.Max(1e-6f, go.transform.lossyScale.x);
            c.radius = worldRadius / scale;
            c.height = (dir.magnitude + worldRadius * 2f) / scale;
        }
    }
}
