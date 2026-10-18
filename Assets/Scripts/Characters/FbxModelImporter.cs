using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assimp;
using Assimp.Unmanaged;
using Shimeji.Core;
using UnityEngine;
using UnityEngine.Rendering;
using NMatrix = System.Numerics.Matrix4x4;
using NQuat = System.Numerics.Quaternion;
using NVec3 = System.Numerics.Vector3;
using UMesh = UnityEngine.Mesh;
using UMaterial = UnityEngine.Material;
using UQuat = UnityEngine.Quaternion;
using UMatrix = UnityEngine.Matrix4x4;

namespace Shimeji.Characters
{
    /// <summary>
    /// 빌드된 앱에서 FBX(및 OBJ/DAE)를 실행 중에 읽는 로더. Assimp(네이티브) + AssimpNetter(MIT).
    /// 노드 계층 → GameObject, 메시/스킨 가중치 → (Skinned)MeshRenderer, 재질 → Shimeji/Toon,
    /// 애니메이션 → legacy AnimationClip. 이후 리그 판별/아바타/애니메이션은 기존 파이프라인이 처리한다.
    /// </summary>
    public sealed class FbxModelImporter : IModelImporter
    {
        public static readonly string[] SupportedExtensions = { ".fbx", ".obj", ".dae" };

        const PostProcessSteps Steps =
            PostProcessSteps.Triangulate |
            PostProcessSteps.JoinIdenticalVertices |
            PostProcessSteps.LimitBoneWeights |
            PostProcessSteps.GenerateSmoothNormals |
            PostProcessSteps.SortByPrimitiveType |
            PostProcessSteps.MakeLeftHanded |       // Assimp(오른손 Y-up) → Unity(왼손)
            PostProcessSteps.FlipWindingOrder;      // 왼손 좌표계에서 앞면이 시계 방향이 되도록

        static readonly object LibGate = new object();

        public bool CanLoad(string path) =>
            Array.IndexOf(SupportedExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        /// <summary>네이티브 assimp.dll을 명시적 경로로 로드 (에디터: Assets/Plugins/x86_64, 빌드: DesktopMascot_Data/Plugins/x86_64).</summary>
        public static void EnsureLibrary()
        {
            lock (LibGate)
            {
                var lib = AssimpLibrary.Instance;
                if (lib.IsLibraryLoaded) return;
                var candidates = new[]
                {
                    Path.Combine(Application.dataPath, "Plugins", "x86_64", "assimp.dll"),
                    Path.Combine(Application.dataPath, "Plugins", "assimp.dll"),
                    Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "assimp.dll"),
                };
                foreach (var c in candidates)
                {
                    if (!File.Exists(c)) continue;
                    if (lib.LoadLibrary(c)) { Log.Info("assimp loaded: " + c); return; }
                }
                throw new FileNotFoundException("assimp.dll not found (FBX loading unavailable). Looked in: " + string.Join("; ", candidates));
            }
        }

        static Scene ImportScene(string path)
        {
            EnsureLibrary();
            using (var ctx = new AssimpContext())
            {
                // 피벗 보존을 끄면 "$AssimpFbx$" 보조 노드가 생기지 않아 본 이름이 그대로 유지된다
                ctx.SetConfig(new Assimp.Configs.BooleanPropertyConfig("IMPORT_FBX_PRESERVE_PIVOTS", false));
                var scene = ctx.ImportFile(path, Steps);
                if (scene == null || scene.RootNode == null) throw new InvalidDataException("assimp returned no scene");
                return scene;
            }
        }

        public async Task<LoadedModel> LoadAsync(string path)
        {
            // 파싱은 네이티브라 백그라운드 스레드에서, Unity 오브젝트 생성은 메인 스레드에서
            var scene = Application.isPlaying ? await Task.Run(() => ImportScene(path)) : ImportScene(path);
            if (!scene.HasMeshes) throw new InvalidDataException("model has no meshes: " + path);
            var b = new SceneBuilder(scene, path);
            var root = b.Build();
            Log.Info($"[fbx] {Path.GetFileName(path)}: nodes={b.NodeCount} meshes={scene.MeshCount} skinned={b.SkinnedCount} " +
                     $"materials={scene.MaterialCount} textures={b.TextureCount} clips={b.ClipCount} columnMatrices={b.ColumnLayout}" +
                     (b.Warnings.Count > 0 ? " warnings: " + string.Join("; ", b.Warnings) : ""));
            return new LoadedModel { Root = root, IsVrm = false, Resources = new ObjectDisposer(b.CreatedAssets) };
        }

        /// <summary>animations/ 폴더용: FBX의 애니메이션만 클립으로. 이름이 일반적이면 파일 이름을 쓴다.</summary>
        public static async Task<List<AnimationClip>> LoadAnimationClipsAsync(string path)
        {
            var scene = Application.isPlaying ? await Task.Run(() => ImportScene(path)) : ImportScene(path);
            var b = new SceneBuilder(scene, path);
            return b.BuildClipsOnly();
        }

        public static bool IsGenericClipName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var n = name.Trim().ToLowerInvariant();
            int bar = n.LastIndexOf('|');
            if (bar >= 0) n = n.Substring(bar + 1);
            return n == "mixamo.com" || n.StartsWith("take") || n == "unreal take" || n == "scene" || n == "default" ||
                   n == "animation" || n == "anim" || n == "armatureaction" || n == "action" || n == "clip";
        }

        sealed class ObjectDisposer : IDisposable
        {
            readonly List<UnityEngine.Object> _objs;
            public ObjectDisposer(List<UnityEngine.Object> objs) { _objs = objs; }
            public void Dispose() { foreach (var o in _objs) UnityGlue.SafeDestroy(o); }
        }

        // ==================================================================
        // Assimp 장면 → Unity
        // ==================================================================

        sealed class SceneBuilder
        {
            const string RootName = "Root";
            readonly Scene _scene;
            readonly string _path, _dir, _stem;
            readonly Dictionary<string, Transform> _byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            readonly Dictionary<string, string> _pathByName = new Dictionary<string, string>(StringComparer.Ordinal);
            readonly Dictionary<int, UMaterial> _materials = new Dictionary<int, UMaterial>();
            readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            readonly HashSet<string> _usedNames = new HashSet<string>(StringComparer.Ordinal);
            readonly List<(Assimp.Mesh mesh, Transform node)> _meshPlacements = new List<(Assimp.Mesh, Transform)>();
            Shader _toon;

            public readonly List<UnityEngine.Object> CreatedAssets = new List<UnityEngine.Object>();
            public readonly List<string> Warnings = new List<string>();
            public bool ColumnLayout { get; }
            public int NodeCount, SkinnedCount, ClipCount;
            public int TextureCount => _textures.Count;

            public SceneBuilder(Scene scene, string path)
            {
                _scene = scene;
                _path = path;
                _dir = Path.GetDirectoryName(path) ?? ".";
                _stem = Path.GetFileNameWithoutExtension(path);
                ColumnLayout = DetectColumnLayout(scene.RootNode);
            }

            // --- 행렬 규약 ---
            // System.Numerics는 행 벡터(이동 = M41..M43). Assimp 원본은 열 벡터(이동 = M14..M34).
            // 래퍼 버전에 따라 전치 여부가 다를 수 있으므로 장면의 노드 행렬을 보고 판별한다.
            static bool DetectColumnLayout(Node root)
            {
                int row = 0, col = 0;
                void Visit(Node n)
                {
                    var m = n.Transform;
                    bool rowT = Math.Abs(m.M41) + Math.Abs(m.M42) + Math.Abs(m.M43) > 1e-6f;
                    bool colT = Math.Abs(m.M14) + Math.Abs(m.M24) + Math.Abs(m.M34) > 1e-6f;
                    if (rowT && !colT) row++;
                    if (colT && !rowT) col++;
                    foreach (var c in n.Children) Visit(c);
                }
                Visit(root);
                return col > row;
            }

            NMatrix Norm(NMatrix m) => ColumnLayout ? NMatrix.Transpose(m) : m;

            static UMatrix ToUnity(NMatrix m) // 행 벡터 규약 m → Unity(열 벡터) 행렬
            {
                var u = new UMatrix();
                u.m00 = m.M11; u.m01 = m.M21; u.m02 = m.M31; u.m03 = m.M41;
                u.m10 = m.M12; u.m11 = m.M22; u.m12 = m.M32; u.m13 = m.M42;
                u.m20 = m.M13; u.m21 = m.M23; u.m22 = m.M33; u.m23 = m.M43;
                u.m30 = m.M14; u.m31 = m.M24; u.m32 = m.M34; u.m33 = m.M44;
                return u;
            }

            static void Apply(Transform t, NMatrix m)
            {
                if (NMatrix.Decompose(m, out var s, out var q, out var p))
                {
                    t.localPosition = new Vector3(p.X, p.Y, p.Z);
                    t.localRotation = new UQuat(q.X, q.Y, q.Z, q.W);
                    t.localScale = new Vector3(s.X, s.Y, s.Z);
                }
                else
                {
                    t.localPosition = new Vector3(m.M41, m.M42, m.M43);
                }
            }

            string Unique(string name)
            {
                if (string.IsNullOrEmpty(name)) name = "node";
                var n = name;
                for (int i = 1; !_usedNames.Add(n); i++) n = name + "_" + i;
                return n;
            }

            // --- 계층 ---

            public GameObject Build()
            {
                _toon = Shader.Find(GltfModelImporter.ToonShaderName) ?? Shader.Find("Standard");
                var wrapper = new GameObject(_stem); // Assemble이 이 오브젝트의 위치/회전을 초기화하므로 루트 노드 변환은 한 단계 아래에 둔다
                _usedNames.Add(wrapper.name);
                try
                {
                    BuildNode(_scene.RootNode, wrapper.transform, isRoot: true, path: "");
                    foreach (var (mesh, node) in _meshPlacements) BuildMesh(mesh, node);
                    var clips = BuildClips();
                    if (clips.Count > 0)
                    {
                        var anim = wrapper.AddComponent<UnityEngine.Animation>();
                        anim.playAutomatically = false;
                        foreach (var c in clips) anim.AddClip(c, c.name);
                        anim.clip = clips[0];
                    }
                    return wrapper;
                }
                catch
                {
                    UnityGlue.SafeDestroy(wrapper);
                    foreach (var o in CreatedAssets) UnityGlue.SafeDestroy(o);
                    throw;
                }
            }

            void BuildNode(Node n, Transform parent, bool isRoot, string path)
            {
                var name = isRoot ? RootName : Unique(n.Name);
                var go = new GameObject(name);
                go.transform.SetParent(parent, false);
                Apply(go.transform, Norm(n.Transform));
                NodeCount++;
                var nodePath = string.IsNullOrEmpty(path) ? name : path + "/" + name;
                if (!string.IsNullOrEmpty(n.Name) && !_byName.ContainsKey(n.Name))
                {
                    _byName[n.Name] = go.transform;
                    _pathByName[n.Name] = nodePath;
                }
                if (n.HasMeshes)
                    foreach (var mi in n.MeshIndices)
                        if (mi >= 0 && mi < _scene.MeshCount) _meshPlacements.Add((_scene.Meshes[mi], go.transform));
                foreach (var c in n.Children) BuildNode(c, go.transform, false, nodePath);
            }

            // --- 메시 ---

            void BuildMesh(Assimp.Mesh am, Transform node)
            {
                if (!am.HasVertices || !am.HasFaces) return;
                var tris = new List<int>(am.FaceCount * 3);
                foreach (var f in am.Faces)
                    if (f.IndexCount == 3) { tris.Add(f.Indices[0]); tris.Add(f.Indices[1]); tris.Add(f.Indices[2]); }
                if (tris.Count == 0) return; // 점/선만 있는 메시

                int vc = am.VertexCount;
                var verts = new Vector3[vc];
                for (int i = 0; i < vc; i++) { var v = am.Vertices[i]; verts[i] = new Vector3(v.X, v.Y, v.Z); }
                var mesh = new UMesh { name = string.IsNullOrEmpty(am.Name) ? _stem + "_mesh" : am.Name };
                mesh.indexFormat = vc > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.vertices = verts;
                if (am.HasNormals && am.Normals.Count == vc)
                {
                    var nrm = new Vector3[vc];
                    for (int i = 0; i < vc; i++) { var v = am.Normals[i]; nrm[i] = new Vector3(v.X, v.Y, v.Z); }
                    mesh.normals = nrm;
                }
                if (am.HasTextureCoords(0))
                {
                    var ch = am.TextureCoordinateChannels[0];
                    var uv = new Vector2[vc];
                    for (int i = 0; i < vc && i < ch.Count; i++) uv[i] = new Vector2(ch[i].X, ch[i].Y);
                    mesh.uv = uv;
                }
                mesh.SetTriangles(tris, 0);
                if (!am.HasNormals) mesh.RecalculateNormals();
                CreatedAssets.Add(mesh);

                var go = new GameObject(Unique(node.name + "_mesh"));
                go.transform.SetParent(node, false);
                var material = GetMaterial(am.MaterialIndex);

                if (am.HasBones)
                {
                    var bones = new List<Transform>();
                    var bind = new List<UMatrix>();
                    var perVertex = new List<(int bone, float w)>[vc];
                    foreach (var bone in am.Bones)
                    {
                        if (!_byName.TryGetValue(bone.Name, out var bt))
                        {
                            Warnings.Add("bone node not found: " + bone.Name);
                            continue;
                        }
                        int bi = bones.Count;
                        bones.Add(bt);
                        bind.Add(ToUnity(Norm(bone.OffsetMatrix)));
                        foreach (var vw in bone.VertexWeights)
                        {
                            if (vw.VertexID < 0 || vw.VertexID >= vc || vw.Weight <= 0f) continue;
                            (perVertex[vw.VertexID] ??= new List<(int, float)>()).Add((bi, vw.Weight));
                        }
                    }
                    // 가중치가 없는 정점은 메시 노드에 고정 (원점으로 찌그러지는 것 방지)
                    int selfBone = bones.Count;
                    bones.Add(go.transform);
                    bind.Add(UMatrix.identity);
                    var weights = new BoneWeight[vc];
                    for (int i = 0; i < vc; i++)
                    {
                        var list = perVertex[i];
                        if (list == null || list.Count == 0) { weights[i] = new BoneWeight { boneIndex0 = selfBone, weight0 = 1f }; continue; }
                        list.Sort((a, b) => b.w.CompareTo(a.w));
                        float sum = 0f;
                        int n = Math.Min(4, list.Count);
                        for (int k = 0; k < n; k++) sum += list[k].w;
                        var bw = new BoneWeight();
                        if (n > 0) { bw.boneIndex0 = list[0].bone; bw.weight0 = list[0].w / sum; }
                        if (n > 1) { bw.boneIndex1 = list[1].bone; bw.weight1 = list[1].w / sum; }
                        if (n > 2) { bw.boneIndex2 = list[2].bone; bw.weight2 = list[2].w / sum; }
                        if (n > 3) { bw.boneIndex3 = list[3].bone; bw.weight3 = list[3].w / sum; }
                        weights[i] = bw;
                    }
                    mesh.boneWeights = weights;
                    mesh.bindposes = bind.ToArray();
                    mesh.RecalculateBounds();
                    var smr = go.AddComponent<SkinnedMeshRenderer>();
                    smr.sharedMesh = mesh;
                    smr.bones = bones.ToArray();
                    smr.rootBone = bones.Count > 1 ? bones[0] : go.transform;
                    smr.sharedMaterial = material;
                    smr.updateWhenOffscreen = true;
                    SkinnedCount++;
                }
                else
                {
                    mesh.RecalculateBounds();
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = material;
                }
            }

            // --- 재질 / 텍스처 ---

            UMaterial GetMaterial(int index)
            {
                if (_materials.TryGetValue(index, out var cached)) return cached;
                var mat = new UMaterial(_toon) { name = _stem + "_mat" + index };
                CreatedAssets.Add(mat);
                _materials[index] = mat;
                if (index < 0 || index >= _scene.MaterialCount) return mat;

                var am = _scene.Materials[index];
                if (am.HasName) mat.name = am.Name;
                var c = am.HasColorDiffuse ? am.ColorDiffuse : new System.Numerics.Vector4(1, 1, 1, 1);
                float opacity = am.HasOpacity ? am.Opacity : 1f;
                if (opacity < 0.05f) opacity = 1f; // 일부 익스포터가 투명도를 0으로 잘못 기록한다
                Texture2D tex = am.HasTextureDiffuse ? ResolveTexture(am.TextureDiffuse.FilePath) : null;
                if (tex != null)
                {
                    mat.mainTexture = tex;
                    // 텍스처가 있으면 확산색은 보통 곱하지 않아도 된다(익스포터가 0.8 회색을 넣는 경우가 많음)
                    c = new System.Numerics.Vector4(1, 1, 1, c.W);
                }
                mat.color = new Color(c.X, c.Y, c.Z, opacity);

                if (opacity < 0.99f)
                {
                    mat.EnableKeyword("_ALPHABLEND_ON");
                    mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetFloat("_ZWrite", 0f);
                    mat.renderQueue = 3000;
                }
                else if (tex != null && ImageDecoders.HasTransparency(tex))
                {
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.SetFloat("_Cutoff", 0.5f);
                    mat.renderQueue = 2450;
                }
                return mat;
            }

            Texture2D ResolveTexture(string filePath)
            {
                if (string.IsNullOrWhiteSpace(filePath)) return null;
                if (_textures.TryGetValue(filePath, out var cached)) return cached;
                Texture2D tex = null;
                try
                {
                    var emb = _scene.GetEmbeddedTexture(filePath);
                    if (emb != null) tex = DecodeEmbedded(emb, filePath);
                    else
                    {
                        var file = FindTextureFile(filePath);
                        if (file != null) tex = ImageDecoders.Decode(File.ReadAllBytes(file), file);
                        if (file == null) Warnings.Add("texture not found: " + filePath);
                        else if (tex == null) Warnings.Add("texture format not supported (use png/jpg/tga/bmp): " + Path.GetFileName(file));
                    }
                }
                catch (Exception e)
                {
                    Warnings.Add($"texture '{filePath}' failed: {e.Message}");
                }
                if (tex != null) CreatedAssets.Add(tex);
                _textures[filePath] = tex;
                return tex;
            }

            static Texture2D DecodeEmbedded(EmbeddedTexture emb, string name)
            {
                if (emb.IsCompressed) return ImageDecoders.Decode(emb.CompressedData, "." + emb.CompressedFormatHint);
                if (!emb.HasNonCompressedData) return null;
                int w = emb.Width, h = emb.Height;
                var src = emb.NonCompressedData;
                var px = new Color32[w * h];
                // Assimp 무압축 텍셀은 위 행부터 → Unity는 아래 행부터
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var t = src[y * w + x];
                        px[(h - 1 - y) * w + x] = new Color32(t.R, t.G, t.B, t.A);
                    }
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = name };
                tex.SetPixels32(px);
                tex.Apply(true);
                return tex;
            }

            /// <summary>FBX에 기록된 텍스처 경로는 만든 PC 기준인 경우가 많아서 여러 위치를 찾아본다.</summary>
            string FindTextureFile(string recorded)
            {
                var rec = recorded.Replace('\\', '/');
                var fileName = Path.GetFileName(rec);
                var stem = Path.GetFileNameWithoutExtension(rec);
                var candidates = new List<string>();
                try { if (Path.IsPathRooted(rec)) candidates.Add(rec); } catch (ArgumentException) { }
                try { candidates.Add(Path.GetFullPath(Path.Combine(_dir, rec))); } catch (Exception) { }
                foreach (var sub in new[] { "", "textures", "Textures", "texture", "tex", _stem + ".fbm", "images" })
                    candidates.Add(Path.Combine(_dir, sub, fileName));
                foreach (var c in candidates)
                    if (File.Exists(c)) return c;
                // 같은 이름(확장자만 다른) 파일 또는 하위 폴더 검색
                try
                {
                    foreach (var f in Directory.GetFiles(_dir, "*.*", SearchOption.AllDirectories))
                    {
                        var fs = Path.GetFileNameWithoutExtension(f);
                        if (!string.Equals(fs, stem, StringComparison.OrdinalIgnoreCase)) continue;
                        var ext = Path.GetExtension(f).ToLowerInvariant();
                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga" || ext == ".bmp") return f;
                    }
                }
                catch (Exception) { /* 권한 등 */ }
                return null;
            }

            // --- 애니메이션 ---

            List<AnimationClip> BuildClips()
            {
                var clips = new List<AnimationClip>();
                if (!_scene.HasAnimations) return clips;
                var built = new List<(AnimationClip clip, string srcName, bool moving)>();
                foreach (var a in _scene.Animations)
                {
                    if (!a.HasNodeAnimations) continue;
                    var clip = new AnimationClip { legacy = true, wrapMode = WrapMode.Loop, frameRate = 30f };
                    bool moving = false;
                    double tps = a.TicksPerSecond > 0 ? a.TicksPerSecond : 30.0;
                    int channels = 0;
                    foreach (var ch in a.NodeAnimationChannels)
                    {
                        if (!_pathByName.TryGetValue(ch.NodeName, out var path)) continue;
                        channels++;
                        if (ch.HasPositionKeys)
                        {
                            var cx = new AnimationCurve(); var cy = new AnimationCurve(); var cz = new AnimationCurve();
                            var p0 = ch.PositionKeys[0].Value;
                            foreach (var k in ch.PositionKeys)
                            {
                                if (!moving && (k.Value - p0).LengthSquared() > 1e-6f * Math.Max(1f, p0.LengthSquared())) moving = true;
                                float t = (float)(k.Time / tps);
                                cx.AddKey(t, k.Value.X); cy.AddKey(t, k.Value.Y); cz.AddKey(t, k.Value.Z);
                            }
                            clip.SetCurve(path, typeof(Transform), "localPosition.x", cx);
                            clip.SetCurve(path, typeof(Transform), "localPosition.y", cy);
                            clip.SetCurve(path, typeof(Transform), "localPosition.z", cz);
                        }
                        if (ch.HasRotationKeys)
                        {
                            var qx = new AnimationCurve(); var qy = new AnimationCurve(); var qz = new AnimationCurve(); var qw = new AnimationCurve();
                            NQuat prev = NQuat.Identity;
                            bool first = true;
                            foreach (var k in ch.RotationKeys)
                            {
                                var q = k.Value;
                                if (!moving && !first && Math.Abs(NQuat.Dot(prev, q)) < 0.99999f) moving = true;
                                // 보간 시 반대 방향으로 도는 것을 막기 위해 부호 연속성 유지
                                if (!first && NQuat.Dot(prev, q) < 0f) q = new NQuat(-q.X, -q.Y, -q.Z, -q.W);
                                prev = q; first = false;
                                float t = (float)(k.Time / tps);
                                qx.AddKey(t, q.X); qy.AddKey(t, q.Y); qz.AddKey(t, q.Z); qw.AddKey(t, q.W);
                            }
                            clip.SetCurve(path, typeof(Transform), "localRotation.x", qx);
                            clip.SetCurve(path, typeof(Transform), "localRotation.y", qy);
                            clip.SetCurve(path, typeof(Transform), "localRotation.z", qz);
                            clip.SetCurve(path, typeof(Transform), "localRotation.w", qw);
                        }
                        if (ch.HasScalingKeys)
                        {
                            var sx = new AnimationCurve(); var sy = new AnimationCurve(); var sz = new AnimationCurve();
                            foreach (var k in ch.ScalingKeys)
                            {
                                float t = (float)(k.Time / tps);
                                sx.AddKey(t, k.Value.X); sy.AddKey(t, k.Value.Y); sz.AddKey(t, k.Value.Z);
                            }
                            clip.SetCurve(path, typeof(Transform), "localScale.x", sx);
                            clip.SetCurve(path, typeof(Transform), "localScale.y", sy);
                            clip.SetCurve(path, typeof(Transform), "localScale.z", sz);
                        }
                    }
                    if (channels == 0) { UnityGlue.SafeDestroy(clip); continue; }
                    clip.EnsureQuaternionContinuity();
                    built.Add((clip, a.Name, moving));
                }
                // 여러 테이크 중 전혀 움직이지 않는 것(바인드 포즈용 "Take 001" 등)은 버린다
                if (built.Count > 1 && built.Any(b => b.moving))
                {
                    foreach (var b in built.Where(b => !b.moving)) UnityGlue.SafeDestroy(b.clip);
                    built.RemoveAll(b => !b.moving);
                }
                // 이름: 일반적인 이름("mixamo.com", "Take 001")은 파일 이름으로 바꾼다 (NinjaRun.fbx → "NinjaRun" → Run 키)
                int generic = built.Count(b => IsGenericClipName(b.srcName));
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int g = 0;
                foreach (var b in built)
                {
                    var name = !IsGenericClipName(b.srcName) ? b.srcName : generic > 1 ? _stem + "_" + (++g) : _stem;
                    var baseName = name;
                    for (int i = 2; !names.Add(name); i++) name = baseName + "_" + i;
                    b.clip.name = name;
                    clips.Add(b.clip);
                    CreatedAssets.Add(b.clip);
                    ClipCount++;
                }
                return clips;
            }

            /// <summary>애니메이션 전용 파일: GameObject 없이 노드 경로만 계산해서 클립을 만든다.</summary>
            public List<AnimationClip> BuildClipsOnly()
            {
                void Visit(Node n, string path, bool isRoot)
                {
                    var name = isRoot ? RootName : Unique(n.Name);
                    var p = string.IsNullOrEmpty(path) ? name : path + "/" + name;
                    if (!string.IsNullOrEmpty(n.Name) && !_pathByName.ContainsKey(n.Name)) _pathByName[n.Name] = p;
                    foreach (var c in n.Children) Visit(c, p, false);
                }
                _usedNames.Add(_stem);
                Visit(_scene.RootNode, "", true);
                var clips = BuildClips();
                CreatedAssets.Clear(); // 소유권은 호출자(캐릭터의 Animation)에게
                return clips;
            }
        }
    }
}
