using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shimeji.Core;
using UniGLTF;
using UnityEngine;
using UniVRM10;

namespace Shimeji.Characters
{
    /// <summary>로드된 모델. Root는 씬에 생성된 모델 최상위 오브젝트.</summary>
    public sealed class LoadedModel
    {
        public GameObject Root;
        /// <summary>VRM이면 스프링본 등 VRM 런타임이 붙어 있다.</summary>
        public bool IsVrm;
        /// <summary>2D 이미지 캐릭터면 설정된다.</summary>
        public SpriteInfo Sprite;
        public bool IsSprite => Sprite != null;
        /// <summary>리소스(텍스처/메시) 해제 담당. 캐릭터 제거 시 Dispose.</summary>
        public IDisposable Resources;
    }

    /// <summary>모델 포맷별 런타임 로더 추상화.</summary>
    public interface IModelImporter
    {
        bool CanLoad(string path);
        Task<LoadedModel> LoadAsync(string path);
    }

    sealed class InstanceDisposer : IDisposable
    {
        readonly RuntimeGltfInstance _instance;
        public InstanceDisposer(RuntimeGltfInstance i) { _instance = i; }
        public void Dispose()
        {
            if (_instance != null) _instance.Dispose();
        }
    }

    /// <summary>VRM 0.x / 1.0 (UniVRM VRM-1.0 로더가 0.x를 마이그레이션해서 읽는다).</summary>
    public sealed class VrmModelImporter : IModelImporter
    {
        public bool CanLoad(string path) => string.Equals(Path.GetExtension(path), ".vrm", StringComparison.OrdinalIgnoreCase);

        public async Task<LoadedModel> LoadAsync(string path)
        {
            IAwaitCaller caller = Application.isPlaying ? (IAwaitCaller)new RuntimeOnlyAwaitCaller() : new ImmediateCaller();
            var instance = await Vrm10.LoadPathAsync(path,
                canLoadVrm0X: true,
                controlRigGenerationOption: ControlRigGenerationOption.None,
                showMeshes: true,
                awaitCaller: caller);
            if (instance == null) throw new InvalidDataException("VRM load returned null (not a VRM file?)");
            var gltf = instance.GetComponent<RuntimeGltfInstance>();
            // 모델 내장 LookAt은 커서 추적과 충돌하므로 끈다(시선 추적은 CharacterAnimator가 담당).
            instance.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
            instance.LookAtTarget = null;
            return new LoadedModel { Root = instance.gameObject, IsVrm = true, Resources = new InstanceDisposer(gltf) };
        }
    }

    /// <summary>glTF / GLB (UniGLTF).</summary>
    public sealed class GltfModelImporter : IModelImporter
    {
        /// <summary>
        /// glTF 머티리얼을 "Standard" 대신 경량 셰이더(Shimeji/Toon)로 만든다.
        /// Standard 전체 배리언트를 빌드에 넣으면 첫 사용 시 메모리를 ~80MB 더 쓰기 때문.
        /// </summary>
        public static IMaterialDescriptorGenerator CreateMaterialGenerator()
        {
            var gen = new BuiltInGltfMaterialDescriptorGenerator();
            var toon = Shader.Find(ToonShaderName);
            if (toon != null)
            {
                gen.PbrMaterialImporter.Shader = toon;
                gen.DefaultMaterialImporter.Shader = toon;
            }
            return gen;
        }

        public const string ToonShaderName = "Shimeji/Toon";

        public bool CanLoad(string path)
        {
            var ext = Path.GetExtension(path);
            return string.Equals(ext, ".glb", StringComparison.OrdinalIgnoreCase) || string.Equals(ext, ".gltf", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<LoadedModel> LoadAsync(string path)
        {
            IAwaitCaller caller = Application.isPlaying ? (IAwaitCaller)new RuntimeOnlyAwaitCaller() : new ImmediateCaller();
            var instance = await GltfUtility.LoadAsync(path, caller, CreateMaterialGenerator());
            if (instance == null) throw new InvalidDataException("glTF load returned null");
            instance.ShowMeshes();
            return new LoadedModel { Root = instance.gameObject, IsVrm = false, Resources = new InstanceDisposer(instance) };
        }
    }

    public static class ModelImporterRegistry
    {
        static readonly List<IModelImporter> Importers = new List<IModelImporter>
        {
            new VrmModelImporter(),
            new GltfModelImporter(),
        };

        public static readonly string[] Extensions = { ".vrm", ".glb", ".gltf" };

        public static IModelImporter Find(string path)
        {
            foreach (var i in Importers) if (i.CanLoad(path)) return i;
            return null;
        }

        /// <summary>캐릭터 폴더에서 모델 파일을 찾는다. model.* 우선, 없으면 첫 번째 지원 파일.</summary>
        public static string FindModelFile(string folder)
        {
            if (!Directory.Exists(folder)) return null;
            foreach (var ext in Extensions)
            {
                var p = Path.Combine(folder, "model" + ext);
                if (File.Exists(p)) return p;
            }
            foreach (var f in Directory.GetFiles(folder))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(Extensions, ext) >= 0) return f;
            }
            // 3D 모델이 없으면 2D 이미지 캐릭터: image.* / sprite.* 우선, 그다음 아무 이미지 (icon.png 제외)
            foreach (var n in new[] { "image", "sprite", "character" })
                foreach (var ext in SpriteModelBuilder.ImageExtensions)
                {
                    var p = Path.Combine(folder, n + ext);
                    if (File.Exists(p)) return p;
                }
            foreach (var f in Directory.GetFiles(folder))
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                // icon.png(트레이용), skeleton.png(관절 설정 결과 미리보기)는 캐릭터 그림이 아니다
                if (SpriteModelBuilder.IsImage(f) &&
                    !string.Equals(stem, "icon", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(stem, SpriteSkeletonImage.FileStem, StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            return null;
        }

        public static async Task<List<AnimationClip>> LoadExtraClipsAsync(string folder)
        {
            var clips = new List<AnimationClip>();
            var dir = Path.Combine(folder, "animations");
            if (!Directory.Exists(dir)) return clips;
            foreach (var f in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".glb" && ext != ".gltf") continue;
                try
                {
                    IAwaitCaller caller = Application.isPlaying ? (IAwaitCaller)new RuntimeOnlyAwaitCaller() : new ImmediateCaller();
                    var inst = await GltfUtility.LoadAsync(f, caller, GltfModelImporter.CreateMaterialGenerator());
                    foreach (var c in inst.AnimationClips)
                    {
                        if (c == null) continue;
                        var copy = UnityEngine.Object.Instantiate(c);
                        copy.name = string.IsNullOrEmpty(c.name) ? Path.GetFileNameWithoutExtension(f) : c.name;
                        copy.legacy = true;
                        clips.Add(copy);
                    }
                    inst.Dispose();
                }
                catch (Exception e)
                {
                    Log.Warn($"animation file '{f}' skipped: {e.Message}");
                }
            }
            return clips;
        }
    }
}
