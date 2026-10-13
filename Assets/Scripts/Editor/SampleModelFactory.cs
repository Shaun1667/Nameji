using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Shimeji.EditorTools
{
    /// <summary>
    /// 라이선스 걱정 없는 자체 제작 테스트 모델.
    ///  - SampleChibi: VRM식 본 이름(J_Bip_*)의 SD 휴머노이드 (+ 머리카락 스프링 체인, 내장 클립 "chibi_wave")
    ///  - SampleBlob : 팔 한 마디/척추 없는 제네릭 생물 (내장 클립 "Blob|Idle", "Blob|Walk")
    /// 기본 도형을 본마다 리지드 스키닝한 단일 SkinnedMesh. 정면 +Z, 키 1.0.
    /// </summary>
    public static class SampleModelFactory
    {
        sealed class Builder
        {
            public readonly GameObject Root;
            public readonly List<Transform> Bones = new List<Transform>();
            readonly Dictionary<string, Transform> _byName = new Dictionary<string, Transform>();
            readonly List<Vector3> _verts = new List<Vector3>();
            readonly List<Vector3> _normals = new List<Vector3>();
            readonly List<BoneWeight> _weights = new List<BoneWeight>();
            readonly List<List<int>> _tris = new List<List<int>>();
            readonly List<Material> _materials = new List<Material>();

            public Builder(string name)
            {
                Root = new GameObject(name);
            }

            public Transform Bone(string name, string parent, Vector3 worldPos)
            {
                var t = new GameObject(name).transform;
                t.SetParent(parent == null ? Root.transform : _byName[parent], false);
                t.position = worldPos;
                t.rotation = Quaternion.identity;
                _byName[name] = t;
                Bones.Add(t);
                return t;
            }

            public int Material(string name, Color color)
            {
                var m = new Material(Shader.Find("Standard")) { name = name, color = color };
                m.SetFloat("_Glossiness", 0.15f);
                _materials.Add(m);
                _tris.Add(new List<int>());
                return _materials.Count - 1;
            }

            static Mesh Primitive(PrimitiveType type)
            {
                var go = GameObject.CreatePrimitive(type);
                var mesh = go.GetComponent<MeshFilter>().sharedMesh;
                Object.DestroyImmediate(go);
                return mesh;
            }

            void Append(Mesh src, Matrix4x4 m, string bone, int mat)
            {
                int bi = Bones.IndexOf(_byName[bone]);
                int baseIndex = _verts.Count;
                var v = src.vertices;
                var n = src.normals;
                for (int i = 0; i < v.Length; i++)
                {
                    _verts.Add(m.MultiplyPoint3x4(v[i]));
                    _normals.Add(m.MultiplyVector(n[i]).normalized);
                    _weights.Add(new BoneWeight { boneIndex0 = bi, weight0 = 1f });
                }
                foreach (var t in src.triangles) _tris[mat].Add(baseIndex + t);
            }

            public void Sphere(string bone, Vector3 center, Vector3 size, int mat) =>
                Append(Primitive(PrimitiveType.Sphere), Matrix4x4.TRS(center, Quaternion.identity, size), bone, mat);

            public void Box(string bone, Vector3 center, Vector3 size, int mat) =>
                Append(Primitive(PrimitiveType.Cube), Matrix4x4.TRS(center, Quaternion.identity, size), bone, mat);

            /// <summary>a→b 선분을 감싸는 캡슐.</summary>
            public void Capsule(string bone, Vector3 a, Vector3 b, float r, int mat)
            {
                var d = b - a;
                var rot = d.sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(Vector3.up, d.normalized) : Quaternion.identity;
                var size = new Vector3(r * 2, (d.magnitude + r * 2) * 0.5f, r * 2);
                Append(Primitive(PrimitiveType.Capsule), Matrix4x4.TRS((a + b) * 0.5f, rot, size), bone, mat);
            }

            /// <summary>원뿔대 (치마 등). a에서 반지름 ra, b에서 rb.</summary>
            public void Frustum(string bone, Vector3 a, Vector3 b, float ra, float rb, int mat, int segments = 16)
            {
                int bi = Bones.IndexOf(_byName[bone]);
                int baseIndex = _verts.Count;
                for (int i = 0; i <= segments; i++)
                {
                    float ang = i * Mathf.PI * 2 / segments;
                    var dir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
                    _verts.Add(a + dir * ra); _normals.Add(dir); _weights.Add(new BoneWeight { boneIndex0 = bi, weight0 = 1 });
                    _verts.Add(b + dir * rb); _normals.Add(dir); _weights.Add(new BoneWeight { boneIndex0 = bi, weight0 = 1 });
                }
                for (int i = 0; i < segments; i++)
                {
                    int k = baseIndex + i * 2;
                    // 양면
                    _tris[mat].AddRange(new[] { k, k + 1, k + 2, k + 1, k + 3, k + 2 });
                    _tris[mat].AddRange(new[] { k, k + 2, k + 1, k + 1, k + 2, k + 3 });
                }
            }

            public SkinnedMeshRenderer Finish()
            {
                var mesh = new Mesh { name = Root.name + "_Mesh" };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.SetVertices(_verts);
                mesh.SetNormals(_normals);
                mesh.boneWeights = _weights.ToArray();
                mesh.subMeshCount = _tris.Count;
                for (int i = 0; i < _tris.Count; i++) mesh.SetTriangles(_tris[i], i);
                var bind = new Matrix4x4[Bones.Count];
                for (int i = 0; i < Bones.Count; i++) bind[i] = Bones[i].worldToLocalMatrix * Root.transform.localToWorldMatrix;
                mesh.bindposes = bind;
                mesh.RecalculateBounds();

                var go = new GameObject("Body");
                go.transform.SetParent(Root.transform, false);
                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = Bones.ToArray();
                smr.rootBone = Bones[0];
                smr.sharedMaterials = _materials.ToArray();
                smr.updateWhenOffscreen = true;
                return smr;
            }
        }

        public static GameObject CreateChibi(bool withEmbeddedClip = true)
        {
            var b = new Builder("SampleChibi");
            int skin = b.Material("Skin", new Color(1f, 0.87f, 0.78f));
            int hair = b.Material("Hair", new Color(0.42f, 0.30f, 0.62f));
            int cloth = b.Material("Cloth", new Color(0.95f, 0.45f, 0.52f));
            int dark = b.Material("Dark", new Color(0.12f, 0.1f, 0.15f));
            int white = b.Material("White", new Color(0.97f, 0.97f, 1f));

            b.Bone("J_Bip_C_Hips", null, new Vector3(0, 0.36f, 0));
            b.Bone("J_Bip_C_Spine", "J_Bip_C_Hips", new Vector3(0, 0.42f, 0));
            b.Bone("J_Bip_C_Chest", "J_Bip_C_Spine", new Vector3(0, 0.50f, 0));
            b.Bone("J_Bip_C_Neck", "J_Bip_C_Chest", new Vector3(0, 0.575f, 0));
            b.Bone("J_Bip_C_Head", "J_Bip_C_Neck", new Vector3(0, 0.60f, 0));
            b.Bone("J_Sec_Hair1_01", "J_Bip_C_Head", new Vector3(0, 0.84f, -0.17f));
            b.Bone("J_Sec_Hair1_02", "J_Sec_Hair1_01", new Vector3(0, 0.74f, -0.25f));
            b.Bone("J_Sec_Hair1_03", "J_Sec_Hair1_02", new Vector3(0, 0.63f, -0.28f));
            b.Bone("J_Sec_Hair1_end", "J_Sec_Hair1_03", new Vector3(0, 0.52f, -0.28f));
            foreach (var (side, sx) in new[] { ("L", -1f), ("R", 1f) })
            {
                b.Bone($"J_Bip_{side}_Shoulder", "J_Bip_C_Chest", new Vector3(sx * 0.03f, 0.555f, 0));
                b.Bone($"J_Bip_{side}_UpperArm", $"J_Bip_{side}_Shoulder", new Vector3(sx * 0.08f, 0.555f, 0));
                b.Bone($"J_Bip_{side}_LowerArm", $"J_Bip_{side}_UpperArm", new Vector3(sx * 0.165f, 0.555f, 0));
                b.Bone($"J_Bip_{side}_Hand", $"J_Bip_{side}_LowerArm", new Vector3(sx * 0.24f, 0.555f, 0));
                b.Bone($"J_Bip_{side}_UpperLeg", "J_Bip_C_Hips", new Vector3(sx * 0.055f, 0.33f, 0));
                b.Bone($"J_Bip_{side}_LowerLeg", $"J_Bip_{side}_UpperLeg", new Vector3(sx * 0.055f, 0.19f, 0));
                b.Bone($"J_Bip_{side}_Foot", $"J_Bip_{side}_LowerLeg", new Vector3(sx * 0.055f, 0.05f, 0));
                b.Bone($"J_Bip_{side}_ToeBase", $"J_Bip_{side}_Foot", new Vector3(sx * 0.055f, 0.015f, 0.07f));
            }

            // 머리 (SD: 키의 40%)
            b.Sphere("J_Bip_C_Head", new Vector3(0, 0.79f, 0), Vector3.one * 0.40f, skin);
            b.Sphere("J_Bip_C_Head", new Vector3(0, 0.83f, -0.02f), new Vector3(0.43f, 0.36f, 0.42f), hair);
            b.Box("J_Bip_C_Head", new Vector3(0, 0.88f, 0.15f), new Vector3(0.30f, 0.09f, 0.08f), hair); // 앞머리
            b.Sphere("J_Bip_C_Head", new Vector3(-0.07f, 0.77f, 0.18f), new Vector3(0.06f, 0.08f, 0.04f), dark);
            b.Sphere("J_Bip_C_Head", new Vector3(0.07f, 0.77f, 0.18f), new Vector3(0.06f, 0.08f, 0.04f), dark);
            b.Sphere("J_Bip_C_Head", new Vector3(-0.06f, 0.785f, 0.197f), Vector3.one * 0.02f, white);
            b.Sphere("J_Bip_C_Head", new Vector3(0.08f, 0.785f, 0.197f), Vector3.one * 0.02f, white);
            // 포니테일 (스프링 체인)
            b.Capsule("J_Sec_Hair1_01", new Vector3(0, 0.84f, -0.17f), new Vector3(0, 0.74f, -0.25f), 0.05f, hair);
            b.Capsule("J_Sec_Hair1_02", new Vector3(0, 0.74f, -0.25f), new Vector3(0, 0.63f, -0.28f), 0.045f, hair);
            b.Capsule("J_Sec_Hair1_03", new Vector3(0, 0.63f, -0.28f), new Vector3(0, 0.52f, -0.28f), 0.035f, hair);
            // 몸통 + 치마
            b.Capsule("J_Bip_C_Chest", new Vector3(0, 0.47f, 0), new Vector3(0, 0.56f, 0), 0.075f, cloth);
            b.Capsule("J_Bip_C_Spine", new Vector3(0, 0.40f, 0), new Vector3(0, 0.47f, 0), 0.07f, cloth);
            b.Frustum("J_Bip_C_Hips", new Vector3(0, 0.43f, 0), new Vector3(0, 0.28f, 0), 0.075f, 0.14f, cloth);
            b.Capsule("J_Bip_C_Neck", new Vector3(0, 0.57f, 0), new Vector3(0, 0.60f, 0), 0.03f, skin);
            foreach (var (side, sx) in new[] { ("L", -1f), ("R", 1f) })
            {
                b.Capsule($"J_Bip_{side}_UpperArm", new Vector3(sx * 0.08f, 0.555f, 0), new Vector3(sx * 0.165f, 0.555f, 0), 0.032f, cloth);
                b.Capsule($"J_Bip_{side}_LowerArm", new Vector3(sx * 0.165f, 0.555f, 0), new Vector3(sx * 0.24f, 0.555f, 0), 0.028f, skin);
                b.Sphere($"J_Bip_{side}_Hand", new Vector3(sx * 0.26f, 0.555f, 0), Vector3.one * 0.065f, skin);
                b.Capsule($"J_Bip_{side}_UpperLeg", new Vector3(sx * 0.055f, 0.33f, 0), new Vector3(sx * 0.055f, 0.19f, 0), 0.04f, skin);
                b.Capsule($"J_Bip_{side}_LowerLeg", new Vector3(sx * 0.055f, 0.19f, 0), new Vector3(sx * 0.055f, 0.06f, 0), 0.036f, white);
                b.Box($"J_Bip_{side}_Foot", new Vector3(sx * 0.055f, 0.025f, 0.025f), new Vector3(0.075f, 0.05f, 0.13f), dark);
            }
            b.Finish();

            if (withEmbeddedClip)
            {
                // 모델 내장 클립 우선 규칙 확인용: 오른손 흔들기
                var clip = new AnimationClip { name = "chibi_wave", legacy = true, wrapMode = WrapMode.Loop };
                var upper = "J_Bip_C_Hips/J_Bip_C_Spine/J_Bip_C_Chest/J_Bip_R_Shoulder/J_Bip_R_UpperArm";
                var lower = upper + "/J_Bip_R_LowerArm";
                SetRotationCurve(clip, upper, t => Quaternion.Euler(0, 0, 70f));
                SetRotationCurve(clip, lower, t => Quaternion.Euler(0, 0, 40f + 30f * Mathf.Sin(t * Mathf.PI * 2)));
                var anim = b.Root.AddComponent<Animation>();
                anim.AddClip(clip, clip.name);
                anim.clip = clip;
                anim.playAutomatically = false;
            }
            return b.Root;
        }

        public static GameObject CreateBlob(bool withEmbeddedClips = true)
        {
            var b = new Builder("SampleBlob");
            int body = b.Material("BlobBody", new Color(0.35f, 0.78f, 0.72f));
            int belly = b.Material("Belly", new Color(0.92f, 0.96f, 0.85f));
            int dark = b.Material("Dark", new Color(0.1f, 0.1f, 0.12f));

            b.Bone("Root", null, new Vector3(0, 0.0f, 0));
            b.Bone("Pelvis", "Root", new Vector3(0, 0.25f, 0));
            b.Bone("Body", "Pelvis", new Vector3(0, 0.38f, 0));
            b.Bone("Head", "Body", new Vector3(0, 0.56f, 0));
            b.Bone("Arm_L", "Body", new Vector3(-0.2f, 0.42f, 0));
            b.Bone("Hand_L", "Arm_L", new Vector3(-0.3f, 0.36f, 0));
            b.Bone("Arm_R", "Body", new Vector3(0.2f, 0.42f, 0));
            b.Bone("Hand_R", "Arm_R", new Vector3(0.3f, 0.36f, 0));
            b.Bone("Leg_L", "Pelvis", new Vector3(-0.09f, 0.22f, 0));
            b.Bone("Foot_L", "Leg_L", new Vector3(-0.09f, 0.05f, 0));
            b.Bone("Leg_R", "Pelvis", new Vector3(0.09f, 0.22f, 0));
            b.Bone("Foot_R", "Leg_R", new Vector3(0.09f, 0.05f, 0));
            b.Bone("Tail1", "Pelvis", new Vector3(0, 0.25f, -0.2f));
            b.Bone("Tail2", "Tail1", new Vector3(0, 0.32f, -0.32f));
            b.Bone("Tail3", "Tail2", new Vector3(0, 0.42f, -0.4f));
            b.Bone("Tail_end", "Tail3", new Vector3(0, 0.5f, -0.44f));

            b.Sphere("Body", new Vector3(0, 0.36f, 0), new Vector3(0.46f, 0.42f, 0.42f), body);
            b.Sphere("Body", new Vector3(0, 0.33f, 0.1f), new Vector3(0.3f, 0.3f, 0.26f), belly);
            b.Sphere("Head", new Vector3(0, 0.68f, 0.02f), new Vector3(0.36f, 0.32f, 0.32f), body);
            b.Sphere("Head", new Vector3(-0.075f, 0.71f, 0.16f), new Vector3(0.05f, 0.07f, 0.04f), dark);
            b.Sphere("Head", new Vector3(0.075f, 0.71f, 0.16f), new Vector3(0.05f, 0.07f, 0.04f), dark);
            b.Capsule("Head", new Vector3(-0.1f, 0.8f, 0), new Vector3(-0.15f, 0.92f, 0), 0.035f, body);
            b.Capsule("Head", new Vector3(0.1f, 0.8f, 0), new Vector3(0.15f, 0.92f, 0), 0.035f, body);
            b.Capsule("Arm_L", new Vector3(-0.2f, 0.42f, 0), new Vector3(-0.3f, 0.36f, 0), 0.04f, body);
            b.Capsule("Arm_R", new Vector3(0.2f, 0.42f, 0), new Vector3(0.3f, 0.36f, 0), 0.04f, body);
            b.Capsule("Leg_L", new Vector3(-0.09f, 0.22f, 0), new Vector3(-0.09f, 0.06f, 0), 0.05f, body);
            b.Capsule("Leg_R", new Vector3(0.09f, 0.22f, 0), new Vector3(0.09f, 0.06f, 0), 0.05f, body);
            b.Sphere("Foot_L", new Vector3(-0.09f, 0.03f, 0.03f), new Vector3(0.1f, 0.06f, 0.14f), dark);
            b.Sphere("Foot_R", new Vector3(0.09f, 0.03f, 0.03f), new Vector3(0.1f, 0.06f, 0.14f), dark);
            b.Capsule("Tail1", new Vector3(0, 0.25f, -0.2f), new Vector3(0, 0.32f, -0.32f), 0.04f, body);
            b.Capsule("Tail2", new Vector3(0, 0.32f, -0.32f), new Vector3(0, 0.42f, -0.4f), 0.035f, body);
            b.Capsule("Tail3", new Vector3(0, 0.42f, -0.4f), new Vector3(0, 0.5f, -0.44f), 0.03f, belly);
            b.Finish();

            if (withEmbeddedClips)
            {
                var anim = b.Root.AddComponent<Animation>();
                const string pelvis = "Root/Pelvis";
                const string bodyPath = pelvis + "/Body";

                var idle = new AnimationClip { name = "Blob|Idle", legacy = true, wrapMode = WrapMode.Loop };
                SetScaleCurve(idle, bodyPath, t => new Vector3(1f + 0.04f * Mathf.Sin(t * Mathf.PI * 2), 1f - 0.04f * Mathf.Sin(t * Mathf.PI * 2), 1f), 1.6f);
                anim.AddClip(idle, idle.name);

                var walk = new AnimationClip { name = "Blob|Walk", legacy = true, wrapMode = WrapMode.Loop };
                SetRotationCurve(walk, pelvis + "/Leg_L", t => Quaternion.Euler(-35f * Mathf.Sin(t * Mathf.PI * 2), 0, 0));
                SetRotationCurve(walk, pelvis + "/Leg_R", t => Quaternion.Euler(35f * Mathf.Sin(t * Mathf.PI * 2), 0, 0));
                SetRotationCurve(walk, bodyPath, t => Quaternion.Euler(0, 0, 6f * Mathf.Sin(t * Mathf.PI * 2)));
                anim.AddClip(walk, walk.name);
                anim.clip = idle;
                anim.playAutomatically = false;
            }
            return b.Root;
        }

        static void SetRotationCurve(AnimationClip clip, string path, System.Func<float, Quaternion> f, float length = 1f, int samples = 13)
        {
            var cx = new AnimationCurve(); var cy = new AnimationCurve(); var cz = new AnimationCurve(); var cw = new AnimationCurve();
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)(samples - 1);
                var q = f(t);
                cx.AddKey(t * length, q.x); cy.AddKey(t * length, q.y); cz.AddKey(t * length, q.z); cw.AddKey(t * length, q.w);
            }
            clip.SetCurve(path, typeof(Transform), "localRotation.x", cx);
            clip.SetCurve(path, typeof(Transform), "localRotation.y", cy);
            clip.SetCurve(path, typeof(Transform), "localRotation.z", cz);
            clip.SetCurve(path, typeof(Transform), "localRotation.w", cw);
        }

        static void SetScaleCurve(AnimationClip clip, string path, System.Func<float, Vector3> f, float length = 1f, int samples = 13)
        {
            var cx = new AnimationCurve(); var cy = new AnimationCurve(); var cz = new AnimationCurve();
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)(samples - 1);
                var s = f(t);
                cx.AddKey(t * length, s.x); cy.AddKey(t * length, s.y); cz.AddKey(t * length, s.z);
            }
            clip.SetCurve(path, typeof(Transform), "localScale.x", cx);
            clip.SetCurve(path, typeof(Transform), "localScale.y", cy);
            clip.SetCurve(path, typeof(Transform), "localScale.z", cz);
        }
    }
}
